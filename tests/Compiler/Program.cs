using System.Diagnostics;
using System.Text.Json;
using FarmerScala.Compiler;
using Jint;
using Jint.Native;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Option(string name, string fallback) { int at = Array.IndexOf(args, name); return at < 0 ? fallback : args[at + 1]; }
string scratch = Path.GetFullPath(Option("--scratch", Path.Combine(root, ".local", "compiler-checks")));
string toolchain = Path.GetFullPath(Option("--toolchain", Path.Combine(root, ".local", "toolchain")));
string project = Path.Combine(scratch, "project");
Directory.CreateDirectory(project);
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
using var compiler = new CompilerService(Path.Combine(root, "sdk"), toolchain);
string source = """
case class Crop(name: String, value: Int)
sealed trait Result
case class Successful(crop: Crop) extends Result
case object Empty extends Result
def roundTrip[A: WireCodec](value: A): A = {
  val codec = summon[WireCodec[A]]
  codec.decode(codec.encode(value))
}
assert(roundTrip(Option(Option.empty[Int])) == Some(None))
assert(roundTrip(Option(Option(Option.empty[Int]))) == Some(Some(None)))
assert(roundTrip[Result](Successful(Crop("sum", 3))) == Successful(Crop("sum", 3)))
assert(roundTrip[Result](Empty) == Empty)
assert(roundTrip(Long.MinValue) == Long.MinValue)
assert(roundTrip(Map(Crop("key", 1) -> Vector(1, 2))) == Map(Crop("key", 1) -> Vector(1, 2)))
class Base { def label: String = "base" }
class Derived extends Base
val crops = (1 to 5).map(i => Crop("crop", i)).collect { case c if c.value % 2 == 1 => c.value }
assert(crops.sum == 9)
assert(new Derived().label == "base")
assert(get_world_size() == 4)
assert(move(North))
assert(get_entity_type().contains(Entities.Grass))
assert(get_cost(Unlocks.Speed).get(Items.Hay) == 12)
assert(get_companion().contains((Entities.Tree, (2, 3))))
assert(measure().contains((4, 5)))
val result = spawn_drone(() => Crop("worker", 42)).map(wait_for(_)).get
assert(result == Crop("worker", 42))
quick_print(crops.sum)
""";
File.WriteAllText(Path.Combine(project, "main.scala"), source);
File.WriteAllText(Path.Combine(project, "replay.scala"), """
class Mutable(var value: Int)
val state = new Mutable(7)
val randomValue = scala.util.Random.nextInt()
val millis = System.currentTimeMillis()
val nanos = System.nanoTime()
val drone = spawn_drone(() => {
  assert(state.value == 7)
  state.value = 99
  (randomValue, millis, nanos, state.value)
}).get
state.value = 8
val result = wait_for(drone)
assert(result == (randomValue, millis, nanos, 99))
assert(state.value == 8)
quick_print("scala-replay-ok")
""");
var first = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
if (!first.ok) throw new Exception(JsonSerializer.Serialize(first));
Check(first.ok && File.Exists(first.artifact), "real Scala.js compilation");
Check(File.Exists(first.artifact + ".editor-map.json"), "editor source mapping metadata");
using (var map = JsonDocument.Parse(File.ReadAllText(first.sourceMap!)))
    Check(map.RootElement.GetProperty("sourcesContent").EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Contains("case class Crop")), "source map embeds our source text for relocated packages");
var engine = new Engine();
string? childResult = null;
var actions = new List<string>();
engine.SetValue("__farmerGlobals", new object());
engine.SetValue("__farmerSpawn", new Func<JsValue, string>(callback =>
{
    childResult = engine.Invoke(callback).AsString();
    return "{\"$\":\"drone\",\"id\":\"test1\"}";
}));
engine.SetValue("__farmerAction", new Func<string, string, string>((operation, json) =>
{
    actions.Add(operation);
    using var args = JsonDocument.Parse(json);
    return operation switch
    {
        "get_world_size" => "4",
        "move" => args.RootElement[0].GetProperty("family").GetString() == "Direction" ? "true" : throw new Exception("Direction missing"),
        "get_entity_type" => "{\"$\":\"enum\",\"family\":\"Entities\",\"name\":\"Grass\"}",
        "get_cost" => "{\"$\":\"dict\",\"items\":[[{\"$\":\"enum\",\"family\":\"Items\",\"name\":\"Hay\"},12]]}",
        "get_companion" => "{\"$\":\"tuple\",\"items\":[{\"$\":\"enum\",\"family\":\"Entities\",\"name\":\"Tree\"},{\"$\":\"tuple\",\"items\":[2,3]}]}",
        "measure" => "{\"$\":\"tuple\",\"items\":[4,5]}",
        "wait_for" => childResult ?? throw new Exception("No child result"),
        "quick_print" => "null",
        _ => throw new Exception("Unexpected action " + operation)
    };
}));
engine.Execute(File.ReadAllText(first.artifact!));
engine.Invoke(engine.Evaluate("runFarm"), "main.scala");
Check(actions.Contains("quick_print") && actions.Contains("wait_for"), "typed APIs, classes, case-class drone result and collections execute");
var cached = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(cached.ok && cached.cached && cached.hash == first.hash, "unchanged source returns exact immutable artifact");
File.AppendAllText(first.artifact!, "\n// corrupt test artifact");
var rebuilt = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(rebuilt.ok && !rebuilt.cached && !File.ReadAllText(rebuilt.artifact!).Contains("corrupt test artifact"), "damaged cache artifact is rebuilt before execution");

File.WriteAllText(Path.Combine(project, "main.scala"), "val bad: Int = \"wrong\"\n");
var bad = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(!bad.ok && bad.artifact == null, "failed edit cannot return old successful artifact");
Check(bad.diagnostics.Any(d => d.file == "main.scala" && d.line == 1 && d.column > 1), "compiler error maps to raw editor line and column");
Check(bad.diagnostics.Any(d => d.endColumn > d.column + 1), "compiler caret span is preserved");
File.WriteAllText(Path.Combine(project, "main.scala"), "import helper.Crop\nval c = Crop(17)\nquick_print(c.value)\n");
File.WriteAllText(Path.Combine(project, "helper.scala"), "case class Crop(value: Int)\n");
var fixedBuild = await compiler.CompileAsync(project, "main.scala", Path.Combine(scratch, "build"));
Check(fixedBuild.ok && fixedBuild.hash != first.hash, "compiler recovers in same warm process and supports cross-window imports");
Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixedBuild.artifact!)!, "sources.json")).Contains("helper.scala"), "artifact retains exact source snapshot");
// A project-load error used to leave the service waiting at sbt's interactive
// retry prompt for four minutes, with the game showing only Preparing Scala.
string brokenProject = Path.Combine(scratch, "broken-load");
Directory.CreateDirectory(brokenProject);
Directory.CreateDirectory(Path.Combine(brokenProject, "project"));
File.WriteAllText(Path.Combine(brokenProject, "project", "build.properties"), "sbt.version=1.11.7\n");
File.WriteAllText(Path.Combine(brokenProject, "build.sbt"), "val broken =\n");
var sbtType = typeof(CompilerService).Assembly.GetType("FarmerScala.Compiler.SbtSession")!;
using (var brokenSession = (IDisposable)Activator.CreateInstance(sbtType, brokenProject, toolchain)!)
{
    bool reported = false;
    try
    {
        var ready = (Task)sbtType.GetMethod("ReadyAsync")!.Invoke(brokenSession, new object[] { CancellationToken.None })!;
        await ready.WaitAsync(TimeSpan.FromSeconds(35));
    }
    catch (IOException error) { reported = error.Message.Contains("project loading failed", StringComparison.OrdinalIgnoreCase); }
    Check(reported, "startup load failure reports a diagnostic promptly instead of waiting for invisible input");
    Check((bool)sbtType.GetProperty("HasExited")!.GetValue(brokenSession)!, "failed startup retires its compiler process");
}
File.WriteAllText(Path.Combine(scratch, "replay-artifact.json"), JsonSerializer.Serialize(new { artifact = first.artifact, entry = "replay.scala" }));
Console.WriteLine(JsonSerializer.Serialize(new { checks, firstCompileMs = first.elapsedMs, cachedMs = cached.elapsedMs, failedEditMs = bad.elapsedMs, recoveryMs = fixedBuild.elapsedMs }));

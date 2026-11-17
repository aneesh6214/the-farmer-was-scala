using System.Reflection;
using System.Security.Cryptography;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
var assembly = typeof(FarmerScala.Plugin).Assembly;
var news = assembly.GetType("FarmerScala.ScalaNews", true)!;
string Constant(string name) => (string)news.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
string page = Constant("Page");
string content = (string)news.GetProperty("Content", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
var metadata = new Dictionary<string, string>();
string rendered = MarkdownText.ProcessMetadata(content, metadata);
Check(!metadata.ContainsKey("titlebar"), "mod news omits the menu-bar label while retaining its document heading");
Check(metadata["windowType"] == "side" && !metadata.ContainsKey("windowWidth") && !metadata.ContainsKey("windowHeight"), "mod news uses the responsive native News layout without fixed dimension overrides");
Check(!rendered.Contains("<meta") && rendered.StartsWith("# The Farmer Was Scala\n", StringComparison.Ordinal) && rendered.Contains("## Changelog") && rendered.Contains("### 1.0.0"), "mod introduction heads the page and the embedded changelog is nested below it");
Check(rendered.Contains("The Farmer Was Replaced... by Scala!") && rendered.Contains("You can visit this window to see updates to the mod."), "news contains the requested introduction");
Check(rendered.Contains("[The Farmer Was Replaced](https://store.steampowered.com/app/2060160/The_Farmer_Was_Replaced/)") && rendered.Contains("[Scala 3](https://docs.scala-lang.org/scala3/book/introduction.html)"), "introduction retains native clickable Markdown links");
string videoTag = "![|1000x](video:" + Constant("DemoVideo") + "|";
Check(rendered.Contains("The Farmer Was Replaced... by Scala!\n\n" + videoTag, StringComparison.Ordinal), "native video appears directly below the tagline");
Check(rendered.IndexOf(videoTag, StringComparison.Ordinal) < rendered.IndexOf("Play [The Farmer Was Replaced]", StringComparison.Ordinal), "demo precedes the play description");
string mediaDirectory = (string)news.GetProperty("MediaDirectory", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check(mediaDirectory == Path.Combine(Path.GetDirectoryName(assembly.Location)!, "assets"), "offline media paths resolve relative to the installed plugin");
Check(typeof(VideoUI).GetMethod("Play") != null, "news uses the existing native video player");
Check(rendered.Contains("<b>Editor Enhancements:</b>") && !rendered.Contains("**Editor Enhancements:**"), "changelog group labels use native TMP bold markup");
var route = news.GetMethod("IsModPage", BindingFlags.NonPublic | BindingFlags.Static)!;
bool Owns(string? requested, string? windowName) => (bool)route.Invoke(null, new object?[] { requested, windowName })!;
Check(Owns(page, "scala_mod_news"), "opening the Scala page selects the embedded loader");
Check(Owns("", "scala_mod_news") && Owns(null, "scala_mod_news"), "restart restoration repairs the empty saved page from the failed native load");
Check(Owns("__farmer_scala_empty__", "scala_mod_news"), "legacy placeholder restores through the embedded loader");
Check(!Owns("index", "news_index") && !Owns("", "news_index") && !Owns("update.md", "scala_mod_news"), "official news and unrelated pages retain native loading");
var firstLaunch = news.GetMethod("ShouldOpenOnFirstLaunch", BindingFlags.NonPublic | BindingFlags.Static)!;
bool OpensFirstLaunch(ISet<string>? history) => (bool)firstLaunch.Invoke(null, new object?[] { history })!;
Check(OpensFirstLaunch(null) && OpensFirstLaunch(new HashSet<string>()), "a fresh install opens mod news without requiring an existing title save");
Check(OpensFirstLaunch(new HashSet<string> { "news_index" }), "official news history does not suppress the first mod introduction");
Check(!OpensFirstLaunch(new HashSet<string> { Constant("WindowName") }), "previously seen mod news is not reopened after the player closes it");
var deferredLayout = news.GetMethod("PositionAfterLayout", BindingFlags.NonPublic | BindingFlags.Static)!;
var placement = (System.Collections.IEnumerator)deferredLayout.Invoke(null, new object?[] { null, null })!;
Check(placement.MoveNext() && placement.Current == null, "initial placement yields until the next frame before inspecting the canvas");
Check(!placement.MoveNext(), "a window closed before deferred placement is ignored safely");
var markdown = assembly.GetType("FarmerScala.NewsMarkdown", true)!.GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static)!;
string Format(string text) => (string)markdown.Invoke(null, new object[] { text })!;
Check(Format("**bold** and *italic*; __bold__ and _italic_") == "<b>bold</b> and <i>italic</i>; <b>bold</b> and <i>italic</i>", "Markdown asterisks and underscores produce bold and italic text");
Check(Format("***both***; **bold and *italic***") == "<b><i>both</i></b>; <b>bold and <i>italic</i></b>", "nested emphasis renders without stray delimiters");
Check(Format("[**Scala**](https://example.com/my_path?q=*literal*)") == "[<b>Scala</b>](https://example.com/my_path?q=*literal*)", "emphasis applies to link labels while preserving URL punctuation");
Check(Format(@"`**literal**` and snake_case_name; \*literal\*") == "`**literal**` and snake_case_name; *literal*", "inline code, identifiers and escaped asterisks remain literal");
Check(Format("```scala\nval name = \"**literal**\"\n---\n```\n\n---").EndsWith("```\n\n––––––––––––––––––––––––––––––––––––––––", StringComparison.Ordinal), "code blocks are protected while native horizontal rules are supported");
Check(Format("* list item\n- **label:** text") == "* list item\n- <b>label:</b> text", "list markers remain intact around emphasized labels");
using var icon = assembly.GetManifestResourceStream("FarmerScala.ScalaLogo.png")!;
using var bytes = new MemoryStream();
icon.CopyTo(bytes);
byte[] png = bytes.ToArray();
Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && png[25] == 6, "embedded icon is RGBA PNG with transparency");
var logo = IconAsset.Read(png);
Check(logo.Width == 128 && logo.Height == 128, "embedded logo uses a high-resolution square UI canvas");
Check(Enumerable.Range(0, logo.Width * logo.Height).Count(i => logo.Pixels[i * 4 + 3] > 0 && logo.Pixels[i * 4 + 3] < 255) > 400, "curved Scala edges retain fractional alpha coverage");
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
Check(SHA256.HashData(png).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "assets/scala-logo-toolbar.png")))), "installed assembly embeds the reviewed icon asset");
foreach (string asset in new[] { Constant("DemoVideo"), Constant("DemoPoster") })
    Check(File.Exists(Path.Combine(root, "assets", asset)), "source checkout contains bundled demo asset: " + asset);
Check(new FileInfo(Path.Combine(root, "assets", Constant("DemoVideo"))).Length < 10_000_000, "converted video fits GitHub's free-plan attachment size limit");
using (var changelog = assembly.GetManifestResourceStream("FarmerScala.Changelog.md")!)
using (var reader = new StreamReader(changelog))
    Check(reader.ReadToEnd() == File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), "the plugin embeds the repository changelog verbatim");
Check(rendered.Contains("case classes") && rendered.Contains("Steam achievements and leaderboards are disabled"), "news includes release features and Scala-save restrictions");
Check(typeof(TitleWindow).GetMethod("LoadPage") != null && typeof(MarkdownText).GetMethod("Setup") != null, "native window and Markdown setup APIs remain available");
const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
Check(new[] { "container", "markdownPrefab", "titleWorkspace" }.All(name => typeof(TitleWindow).GetField(name, PrivateInstance) != null) &&
    new[] { "Metadata", "OpenMarkdownText" }.All(name => typeof(TitleWindow).GetProperty(name)!.GetSetMethod(true) != null), "embedded loader can initialize the supported native window without NewsPath");
Check(typeof(TitleWindow).GetMethod("LinkCalled", PrivateInstance) != null, "embedded page retains the native news link handler");
Check(typeof(ColoredButton).GetField("icon", BindingFlags.NonPublic | BindingFlags.Instance) != null && typeof(ColoredButton).GetField("button", BindingFlags.NonPublic | BindingFlags.Instance) != null, "native button retains expected icon and event fields");
Check(typeof(TitleWorkspace).GetMethod("PositionNewWindow", BindingFlags.NonPublic | BindingFlags.Instance) != null, "mod news can reuse the exact native placement routine");
var placementType = assembly.GetType("FarmerScala.NewsButtonPlacement", true)!;
var offsetMethod = placementType.GetMethod("Offset", BindingFlags.NonPublic | BindingFlags.Static)!;
float Offset((float, float, float, float) newsBounds, (float, float, float, float)[] buttons, float gap = 10f)
    => (float)offsetMethod.Invoke(null, new object[] { newsBounds, buttons, gap })!;
var newsBounds = (100f, 0f, 150f, 50f);
Check(Offset(newsBounds, Array.Empty<(float, float, float, float)>()) == -60f, "without DLC icons Scala remains one evenly spaced slot left of news");
Check(Offset(newsBounds, new[] { (40f, 0f, 90f, 50f) }) == -120f, "the native mining hat retains its slot and Scala moves to its left");
Check(Offset(newsBounds, new[] { (-20f, 0f, 30f, 50f), (40f, 0f, 90f, 50f) }) == -180f, "an unsorted row of native buttons is cleared completely");
Check(Offset(newsBounds, new[] { (-500f, 0f, -450f, 50f) }) == -60f, "a DLC icon in the opposite corner does not displace Scala off-screen");
Check(Offset(newsBounds, new[] { (40f, 60f, 90f, 110f) }) == -60f, "buttons in another row do not affect the news row");
Check(Offset(newsBounds, new[] { (160f, 0f, 210f, 50f) }) == -60f, "a native button right of news keeps its existing spacing");
Check(Offset((200f, 0f, 280f, 80f), new[] { (80f, 0f, 184f, 80f) }, 16f) == -216f, "scaled and differently sized native icons preserve the configured gap");
var gapMethod = placementType.GetMethod("NativeGap", BindingFlags.NonPublic | BindingFlags.Static)!;
float NativeGap((float, float, float, float) bounds, (float, float, float, float)[] buttons, float fallback = 10f)
    => (float)gapMethod.Invoke(null, new object[] { bounds, buttons, fallback })!;
var unequal = new[] { (20f, 0f, 70f, 50f) };
Check(NativeGap(newsBounds, unequal) == 30f && Offset(newsBounds, unequal, NativeGap(newsBounds, unequal)) == -160f,
    "Scala uses the native DLC/news gap rather than its smaller estimated gap");
Check(NativeGap(newsBounds, new[] { (20f, 70f, 70f, 120f) }) == 10f, "another row cannot supply menu spacing");
Check(NativeGap(newsBounds, new[] { (-500f, 0f, -450f, 50f) }) == 10f, "a distant DLC button does not create a giant gap");
Check(NativeGap(newsBounds, Array.Empty<(float, float, float, float)>()) == 10f, "without native siblings the fallback gap remains available");
Check(NativeGap(newsBounds, new[] { (150f, 0f, 200f, 50f) }) == 0f, "a native layout with zero gap is preserved");
Console.WriteLine($"{checks} native news metadata, isolation and embedded-icon checks passed; live Unity layout still requires visual verification.");

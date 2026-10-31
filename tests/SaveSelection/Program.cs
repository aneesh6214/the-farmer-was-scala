using System.Reflection;
using FarmerScala;
int checks = 0;
void Check(bool condition,string description)
{
    if (!condition) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
string scratch = Path.Combine(Path.GetTempPath(),"FarmerScala-save-selection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
Helper.persistentDataPath = scratch;
string path = Path.Combine(scratch,"options.txt");
void Restore() => typeof(SaveSelectionRestoration).GetNestedType("LoadedOptions",BindingFlags.NonPublic)!
    .GetMethod("Postfix",BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,null);
try
{
    Restore();
    Check(OptionHolder.Values.Count == 0,"first launch without options leaves native defaults intact");
    foreach (string name in new[] {"main farm 2","8","F5","Scala Demo 8x8","my farm","农场 3"})
    {
        // Simulate the native Unknown -> KeyCombination conversion. Its Enum
        // parser accepts both numeric final words and actual key names.
        OptionHolder.Reset();
        object binding = new object();
        OptionHolder.Values["activeSave"] = binding;
        OptionHolder.Values["start execution"] = binding;
        OptionHolder.Values["volume"] = .5f;
        string content = "start execution = F5\nvolume = 0.5\nactiveSave = " + name + "\n";
        File.WriteAllText(path,content);
        Restore();
        Check(OptionHolder.Values["activeSave"] is string selected && selected == name,"restore exact save name: " + name);
        Check(ReferenceEquals(OptionHolder.Values["start execution"],binding) && (float)OptionHolder.Values["volume"] == .5f,"other typed settings stay intact: " + name);
        Check(File.ReadAllText(path) == content && Directory.GetFiles(scratch).Length == 1,"no options rewrite or save creation: " + name);
    }
    Check(SaveSelectionRestoration.ReadSaveName("activeSave = old\r\nactiveSave = latest\r\n") == "latest","CRLF parsing uses the final persisted value");
    Check(SaveSelectionRestoration.ReadSaveName("activeSave = farm = name\n") == "farm = name","save names may contain equals signs");
    Check(SaveSelectionRestoration.ReadSaveName("volume = 1\n") == null && SaveSelectionRestoration.ReadSaveName("activeSave = \n") == null,"missing or empty selection does not invent a save name");
}
finally { Directory.Delete(scratch,true); } // Unique test-owned scratch directory.
Console.WriteLine($"Save-selection checks passed: {checks}");

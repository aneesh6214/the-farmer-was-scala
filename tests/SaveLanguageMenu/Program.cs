using FarmerScala;
using FarmerScala.Editor;

// This suite executes production patch callbacks with native-flow adapters and
// actual SourceStore files under unique temp roots. It is not an installed-game test.
int passed = 0;
Run("New Game opens native choices without writing", f =>
{
    f.Chooser.CreateNewSave();
    Equal(3, f.Popup.Buttons.Count);
    Equal("Default", f.Popup.Buttons[0].text);
    Equal("Scala 3", f.Popup.Buttons[1].text);
    Equal("×", f.Popup.Buttons[2].text);
    True(f.Popup.Text.Contains("permanent"));
    True(f.Popup.Text.Contains("Steam achievements and leaderboards are disabled in Scala 3."));
    True(f.Popup.Stacked);
    Equal(0, Saver.NativeCreations);
    True(!Directory.Exists(Saver.Root));
});
Run("Close button dismisses choices without creating a save and retires every callback", f =>
{
    f.Chooser.CreateNewSave();
    Action[] stale = f.Popup.Buttons.Select(button => button.callback).ToArray();
    f.Click("×");
    foreach (Action callback in stale)
        callback();
    True(!f.Popup.Open);
    True(!f.Popup.Stacked);
    Equal(0, Saver.NativeCreations);
    True(!Directory.Exists(Saver.Root));
});
Run("A superseded close callback cannot dismiss the current language choices", f =>
{
    f.Chooser.CreateNewSave();
    Action staleBack = f.Popup.Buttons.Single(button => button.text == "×").callback;
    f.Chooser.CreateNewSave();
    staleBack();
    True(f.Popup.Open && f.Popup.Stacked);
    Equal(0, Saver.NativeCreations);
    True(!Directory.Exists(Saver.Root));
    f.Click("Default");
    Equal(1, Saver.NativeCreations);
    Equal(SaveLanguage.Default, f.Store.ReadLanguage(f.Dir("Save0")));
});
Run("Default metadata precedes native creation and native rename editor", f =>
{
    Saver.AfterLanguageBeforeNativeFile = name =>
    {
        Equal(SaveLanguage.Default, f.Store.ReadLanguage(f.Dir(name)));
        True(File.Exists(SourceStore.LanguageFile(f.Dir(name))));
        True(!File.Exists(Saver.GetPathOfSaveFile(name)));
        True(!Directory.Exists(SourceStore.SourceDirectory(f.Dir(name))));
    };
    f.Create("Default");
    var row = f.Chooser.Options["Save0"];
    Equal(1, Saver.NativeCreations);
    Equal(1, f.Chooser.NativeCreateCalls);
    Equal("Save0", row.FileName);
    Equal("Save0", row.Input);
    True(row.Editing);
    Equal("Save0", row.Label);
    True(!row.GetComponent<ScalaSaveBadge>()!.IsScala);
    True(!File.Exists(Path.Combine(SourceStore.ProjectRoot(f.Dir("Save0")), "project.json")));
    True(!f.Popup.Stacked);
});
Run("Scala 3 metadata and blank main precede native save", f =>
{
    Saver.AfterLanguageBeforeNativeFile = name =>
    {
        Equal(SaveLanguage.NativeScala, f.Store.ReadLanguage(f.Dir(name)));
        True(f.Store.ReadManifest(f.Dir(name)).Enabled);
        Equal("", f.Store.ReadSources(f.Dir(name))["main"]);
        True(!File.Exists(Saver.GetPathOfSaveFile(name)));
    };
    f.Create("Scala 3");
    Equal(1, Saver.NativeCreations);
    Equal("Save0", f.Chooser.Options["Save0"].Label);
    True(f.Chooser.Options["Save0"].GetComponent<ScalaSaveBadge>()!.IsScala);
});
Run("Duplicate and superseded choice callbacks are inert", f =>
{
    f.Chooser.CreateNewSave();
    Action superseded = f.Popup.Buttons[1].callback;
    f.Chooser.CreateNewSave();
    Action chosen = f.Popup.Buttons[0].callback;
    superseded();
    Equal(0, Saver.NativeCreations);
    chosen();
    chosen();
    Equal(1, Saver.NativeCreations);
    Equal(SaveLanguage.Default, f.Store.ReadLanguage(f.Dir("Save0")));
});
Run("Fresh naming skips directory-only orphan and legacy file", f =>
{
    Directory.CreateDirectory(f.Dir("Save0"));
    string sentinel = Path.Combine(f.Dir("Save0"), "keep.py");
    File.WriteAllText(sentinel, "keep existing content");
    File.WriteAllText(Path.Combine(Saver.Root, "Save1.json"), "legacy content");
    f.Create("Scala 3");
    True(f.Chooser.Options.ContainsKey("Save2"));
    Equal("keep existing content", File.ReadAllText(sentinel));
    Equal("legacy content", File.ReadAllText(Path.Combine(Saver.Root, "Save1.json")));
    True(!File.Exists(SourceStore.LanguageFile(f.Dir("Save0"))));
});
Run("Interrupted native create retains its own metadata and retry skips it", f =>
{
    Saver.AfterLanguageBeforeNativeFile = _ => throw new IOException("injected native failure");
    f.Create("Scala 3");
    Equal(0, Saver.NativeCreations);
    Equal(0, f.Chooser.Options.Count);
    Equal(SaveLanguage.NativeScala, f.Store.ReadLanguage(f.Dir("Save0")));
    string identity = File.ReadAllText(SourceStore.LanguageFile(f.Dir("Save0")));
    True(f.Popup.Open && f.Popup.Text.Contains("unfinished save data has been kept"));
    Saver.AfterLanguageBeforeNativeFile = null;
    f.Create("Default");
    True(f.Chooser.Options.ContainsKey("Save1"));
    Equal(identity, File.ReadAllText(SourceStore.LanguageFile(f.Dir("Save0"))));
    True(!File.Exists(Saver.GetPathOfSaveFile("Save0")));
});
Run("A directory appearing after name selection aborts before native write", f =>
{
    Saver.ImmediatelyBeforeCreate = name =>
    {
        Directory.CreateDirectory(f.Dir(name));
        File.WriteAllText(Path.Combine(f.Dir(name), "keep.py"), "race data");
    };
    f.Create("Scala 3");
    Equal(0, Saver.NativeCreations);
    Equal("race data", File.ReadAllText(Path.Combine(f.Dir("Save0"), "keep.py")));
    True(!File.Exists(SourceStore.LanguageFile(f.Dir("Save0"))));
    True(!File.Exists(Saver.GetPathOfSaveFile("Save0")));
});
Run("New Scala 3 save never overwrites existing project sources", f =>
{
    f.Store.InitializeLanguage(f.Dir("Save0"), "Save0", SaveLanguage.NativeScala);
    f.Store.SaveSources(f.Dir("Save0"), "Save0", true, new Dictionary<string, string> { ["main"] = "class Existing: pass" });
    File.WriteAllText(Saver.GetPathOfSaveFile("Save0"), "existing save");
    string identity = File.ReadAllText(SourceStore.LanguageFile(f.Dir("Save0")));
    f.Create("Scala 3");
    Equal("class Existing: pass", f.Store.ReadSources(f.Dir("Save0"))["main"]);
    Equal("existing save", File.ReadAllText(Saver.GetPathOfSaveFile("Save0")));
    Equal(identity, File.ReadAllText(SourceStore.LanguageFile(f.Dir("Save0"))));
    True(f.Chooser.Options.ContainsKey("Save1"));
});
Run("Existing default badge is read-only and rename input stays raw", f =>
{
    Directory.CreateDirectory(f.Dir("Existing"));
    File.WriteAllText(Saver.GetPathOfSaveFile("Existing"), "existing save");
    string[] before = Directory.GetFiles(f.Dir("Existing"), "*", SearchOption.AllDirectories);
    var row = new SaveOption();
    row.Setup("Existing", f.Chooser);
    row.Edit();
    Equal("Existing", row.FileName);
    Equal("Existing", row.Input);
    Equal("Existing", row.Label);
    True(!row.GetComponent<ScalaSaveBadge>()!.IsScala);
    Equal(string.Join("|", before), string.Join("|", Directory.GetFiles(f.Dir("Existing"), "*", SearchOption.AllDirectories)));
    True(!Directory.Exists(SourceStore.ProjectRoot(f.Dir("Existing"))));
});
Run("Successful rename preserves language and forwards actual success only", f =>
{
    f.Create("Scala 3");
    string identity = File.ReadAllText(SourceStore.LanguageFile(f.Dir("Save0")));
    var row = f.Chooser.Options["Save0"];
    row.Input = "Renamed farm";
    row.EditFinished();
    Equal("Renamed farm", row.FileName);
    Equal("Renamed farm", row.Label);
    True(row.GetComponent<ScalaSaveBadge>()!.IsScala);
    Equal(identity, File.ReadAllText(SourceStore.LanguageFile(f.Dir("Renamed farm"))));
    Equal(SaveLanguage.NativeScala, f.Store.ReadLanguage(f.Dir("Renamed farm")));
    Equal(1, EditorIntegration.Renames.Count);
    Equal(("Save0", "Renamed farm"), EditorIntegration.Renames[0]);
    Directory.CreateDirectory(f.Dir("Occupied"));
    row.Edit();
    row.Input = "Occupied";
    row.EditFinished();
    Equal("Renamed farm", row.FileName);
    Equal(1, EditorIntegration.Renames.Count);
    Equal("Renamed farm", row.Label);
    True(row.GetComponent<ScalaSaveBadge>()!.IsScala);
});
Run("Corrupt metadata badge preserves files and original edit name", f =>
{
    Directory.CreateDirectory(SourceStore.ProjectRoot(f.Dir("Broken")));
    File.WriteAllText(SourceStore.LanguageFile(f.Dir("Broken")), "not JSON");
    var row = new SaveOption();
    row.Setup("Broken", f.Chooser);
    row.Edit();
    Equal("Broken", row.Input);
    Equal("Broken", row.Label);
    True(!row.GetComponent<ScalaSaveBadge>()!.IsScala);
    Equal("not JSON", File.ReadAllText(SourceStore.LanguageFile(f.Dir("Broken"))));
});
Run("Load failure uses native dismissible popup and console fallback", f =>
{
    SaveLanguageMenu.ShowLoadError("This save has invalid language metadata.");
    Equal("This save has invalid language metadata.", f.Popup.Text);
    Equal(1, f.Popup.Buttons.Count);
    f.Click("OK");
    True(!f.Popup.Open);
    MainSim.Inst = null;
    SaveLanguageMenu.ShowLoadError("load fallback");
    True(Plugin.Instance!.Messages.Contains("load fallback"));
});
Run("Native bootstrap outside a choice remains default and unmodified", f =>
{
    Directory.CreateDirectory(f.Dir("Save0"));
    Equal("Save0", SaveChooser.GenerateUnusedSaveName());
    Saver.CreateNewSaveGame("Bootstrap");
    Equal(1, Saver.NativeCreations);
    Equal(SaveLanguage.Default, f.Store.ReadLanguage(f.Dir("Bootstrap")));
    True(!Directory.Exists(SourceStore.ProjectRoot(f.Dir("Bootstrap"))));
    Equal(0, f.Popup.Shown);
});
Run("Replacing the popup restores native layout and retires choices", f =>
{
    f.Chooser.CreateNewSave();
    Action stale = f.Popup.Buttons[1].callback;
    True(f.Popup.Stacked);
    f.Popup.ShowPopup("An ordinary native warning", [new("OK", f.Popup.Close)]);
    True(!f.Popup.Stacked);
    stale();
    Equal(0, Saver.NativeCreations);
    Equal("An ordinary native warning", f.Popup.Text);
    f.Click("OK");
    True(!f.Popup.Open);
});
Run("Native disable retires language callbacks and restores layout", f =>
{
    f.Chooser.CreateNewSave();
    Action stale = f.Popup.Buttons[0].callback;
    f.Popup.Disable();
    True(!f.Popup.Stacked);
    stale();
    Equal(0, Saver.NativeCreations);
    f.Create("Scala 3");
    Equal(1, Saver.NativeCreations);
});
Run("Chooser regions stay contained, centered and positively separated across bounds", _ =>
{
    foreach (float width in new[] { 120f, 240f, 540f, 1100f })
        foreach (float height in new[] { 100f, 220f, 360f, 800f })
            foreach (float buttonWidth in new[] { 100f, 250f, 620f })
                foreach (float backWidth in new[] { 75f, 250f, 800f })
                {
                    const float padding = 18f, gap = 20f;
                    var layout = new SaveLanguagePopupGeometry(width, height, 66f, buttonWidth, 65f, backWidth, padding, gap);
                    True(float.IsFinite(layout.Width) && float.IsFinite(layout.Height));
                    True(layout.Width >= width && layout.Height > 0f);
                    var regions = new[] { layout.Back, layout.Header, layout.Message, layout.Default, layout.Native };
                    foreach (var region in regions)
                    {
                        True(region.Width > 0f && region.Height > 0f);
                        True(region.Left >= -layout.Width / 2f - 0.001f && region.Right <= layout.Width / 2f + 0.001f);
                        True(region.Bottom >= -layout.Height / 2f - 0.001f && region.Top <= layout.Height / 2f + 0.001f);
                    }
                    for (int first = 0; first < regions.Length; first++)
                        for (int second = first + 1; second < regions.Length; second++)
                            True(regions[first].Right < regions[second].Left || regions[second].Right < regions[first].Left ||
                                 regions[first].Top < regions[second].Bottom || regions[second].Top < regions[first].Bottom);
                    Equal(0f, layout.Message.X);
                    True(layout.Message.Top < layout.Header.Bottom);
                    Near(layout.Width / 2f - 8f, layout.Back.Right);
                    Near(layout.Height / 2f - 8f, layout.Back.Top);
                    Equal(40f, layout.Back.Width);
                    Equal(40f, layout.Back.Height);
                    True(layout.Default.X < 0f && layout.Native.X > 0f);
                    Near(-layout.Default.X, layout.Native.X);
                    Equal(layout.Default.Y, layout.Native.Y);
                    Near(-layout.Height / 2f + 6f + padding, layout.Default.Bottom);
                    Near(layout.Default.Bottom, layout.Native.Bottom);
                    True(layout.Default.Width >= buttonWidth && layout.Native.Width >= buttonWidth);
                    Equal(layout.Default.Width, layout.Native.Width);
                    Equal(65f, layout.Default.Height);
                    Equal(65f, layout.Native.Height);
                    Equal(66f, layout.Message.Height);
                    True(layout.Back.Bottom - layout.Message.Top >= gap - 0.001f);
                    True(layout.Message.Bottom - layout.Default.Top >= gap - 0.001f);
                    True(layout.Native.Left - layout.Default.Right >= gap - 0.001f);
                }
});
Run("A roomy chooser uses a compact header and minimum control sizes", _ =>
{
    var layout = new SaveLanguagePopupGeometry(900f, 600f, 66f, 250f, 65f, 90f, 18f, 20f);
    Equal(900f, layout.Width);
    True(layout.Height < 600f);
    True(layout.Default.Width >= 250f && layout.Native.Width >= 250f);
    Equal(layout.Default.Width, layout.Native.Width);
    Equal(40f, layout.Back.Width);
    Equal(40f, layout.Back.Height);
    Equal(65f, layout.Default.Height);
    Equal(65f, layout.Native.Height);
});
Run("Native oversized panel measurements keep clear visible gaps", _ =>
{
    // Shipped prefab: 200x50 root, 214x64 panel. Reserve the visible size,
    // rather than stacking roots and accidentally overlapping their panels.
    var layout = new SaveLanguagePopupGeometry(540f, 238f, 66f, 214f, 64f, 100f, 24f, 24f);
    True(layout.Height > 238f);
    Equal(64f, layout.Default.Height);
    Equal(64f, layout.Native.Height);
    True(layout.Native.Left - layout.Default.Right >= 24f);
    True(layout.Back.Bottom - layout.Message.Top >= 24f);
    True(layout.Message.Bottom - layout.Default.Top >= 24f);
});
Run("Unusable chooser bounds or spacing fail before producing rectangles", _ =>
{
    for (int field = 0; field < 8; field++)
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        {
            float[] values = [900f, 600f, 66f, 250f, 65f, 90f, 18f, 20f];
            values[field] = invalid;
            Throws<InvalidOperationException>(() => new SaveLanguagePopupGeometry(values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7]));
        }
});
Console.WriteLine($"PASS: {passed} headless save-language menu cases (production patches + real SourceStore; no Unity/game launch)");

void Run(string name, Action<Fixture> test)
{
    using var fixture = new Fixture();
    try
    {
        test(fixture);
        passed++;
        Console.WriteLine("PASS " + name);
    }
    catch (Exception error) { throw new Exception("FAIL " + name, error); }
}
static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
static void Near(float expected, float actual)
{
    if (Math.Abs(expected - actual) > 0.001f)
        throw new Exception($"Expected approximately [{expected}], got [{actual}].");
}
static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException) { return; }
    throw new Exception("Expected " + typeof(TException).Name + ".");
}
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected [{expected}], got [{actual}].");
}

sealed class Fixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FarmerScala-SaveLanguageMenu-" + Guid.NewGuid().ToString("N"));
    public readonly SaveChooser Chooser = new();
    public readonly SourceStore Store;
    public WarningPopup Popup => MainSim.Inst!.warningPopup;
    public Fixture()
    {
        MenuPatches.Reset();
        SaveLanguagePopupLayout.Active = null;
        Saver.Root = Path.Combine(root, "Saves");
        Saver.NativeCreations = 0;
        Saver.ImmediatelyBeforeCreate = null;
        Saver.AfterLanguageBeforeNativeFile = null;
        BepInEx.Paths.CachePath = Path.Combine(root, "cache");
        MainSim.Inst = new();
        Plugin.Instance = new();
        EditorIntegration.Renames.Clear();
        Store = new SourceStore(BepInEx.Paths.CachePath);
    }
    public string Dir(string name) => Saver.GetPathOfSaveDirectory(name);
    public void Click(string label) => Popup.Buttons.Single(button => button.text == label).callback();
    public void Create(string language)
    {
        Chooser.CreateNewSave();
        Click(language);
    }
    public void Dispose()
    {
        // Only this fixture's exact canonical, GUID-named temp root is removed.
        string resolved = Path.GetFullPath(root);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("FarmerScala-SaveLanguageMenu-", StringComparison.Ordinal))
            throw new IOException("Refusing to remove an unexpected test path.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }
}

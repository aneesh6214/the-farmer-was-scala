using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public sealed partial class IntegrationPlugin
{
    private IEnumerator ColdStartEditor()
    {
        yield return Wait(() => MainSim.Inst != null && MainSim.Inst.workspace != null && MainSim.Inst.workspace.codeWindows.ContainsKey("Broken"), "initial broken project load");
        MainSim.Inst.menu.Play(); yield return Frames();
        string original = Main.CodeInput.text;
        var analysis = Mod("Editor.ScalaAnalysis");
        var compiler = Field(Host, "compiler")!;
        bool NoSuccessfulBuild() => ((IDictionary)Field(compiler, "artifactOrigins")!).Count == 0;
        Check(NoSuccessfulBuild() && !Running, "cold editor starts before any successful player compilation");
        object? resolved = null;
        int enumAt = original.IndexOf("Entities.Grass", StringComparison.Ordinal) + "Entities.Gr".Length;
        yield return Wait(() => {
            object?[] args = { Main, enumAt, null };
            if (!(bool)Call(analysis, "TryResolve", null, args)!) return false;
            resolved = args[2]; return !string.IsNullOrEmpty((string?)Field(resolved!, "HoverText"));
        }, "enum case hover in a cold broken project");
        Check(((string)Field(resolved!, "HoverText")!).Contains("Grass"), "enum case documentation is available before a successful build");
        int assertAt = original.IndexOf("assert", StringComparison.Ordinal) + 3;
        yield return Wait(() => {
            object?[] args = { Main, assertAt, null };
            if (!(bool)Call(analysis, "TryResolve", null, args)!) return false;
            resolved = args[2]; return ((string?)Field(resolved!, "HoverText"))?.Contains("assert") == true;
        }, "Scala assert hover in a cold broken project");
        Check(((string)Field(resolved!, "HoverText")!).Contains("Boolean"), "assert resolves its Scala signature despite another module's type error");
        yield return Wait(() => {
            var spans = ((IEnumerable)Call(analysis, "Spans", null, Main)!).Cast<object>().ToArray();
            return spans.Any(s => (int)Field(s, "Start")! == original.IndexOf("Grass", StringComparison.Ordinal) && (string?)Field(s, "Kind") == "builtin") &&
                spans.Any(s => (int)Field(s, "Start")! == original.IndexOf("assert", StringComparison.Ordinal) && (string?)Field(s, "Kind") == "function");
        }, "enum-case and standard-function semantic highlighting before compilation");
        Check(true, "enum cases and assert receive compiler semantic colors in a cold broken project");
        Edit(Main, original + "\nval choice = Entities.\n");
        List<string>? names = null;
        Call(analysis, "Complete", null, Main, Main.CodeInput.text.LastIndexOf("Entities.", StringComparison.Ordinal) + 9, new Action<List<string>>(v => names = v));
        yield return Wait(() => names != null && names.Contains("Grass") && names.Contains("values"), "completion while the initial project is syntactically incomplete");
        Check(names!.IndexOf("Grass") < names.IndexOf("values"), "ranked enum completion works without a prior successful build");
        Edit(Main, original);
        object? diagnostics = null;
        yield return Wait(() => {
            object?[] args = { Main, null };
            if (!(bool)Call(analysis, "TryDiagnostics", null, args)!) return false;
            diagnostics = args[1];
            return ((IEnumerable)diagnostics!).Cast<object>().Any(d => (string?)Field(d, "File") == "Broken");
        }, "diagnostics for the original invalid module");
        Check(NoSuccessfulBuild() && !Running && Sim.farm.drones[0].pos.x == 0, "editor features work without building or executing the player's program");
        // Repair only after every cold-start assertion; the normal smoke case runs next.
        Edit(Main, original.Replace("Entities.Does_Not_Exist", "Entities.Grass"));
        Edit(MainSim.Inst.workspace.codeWindows["Broken"], "val invalid: Int = 1\n");
    }
}

using System.Reflection;
using FarmerScala.Editor;
using UnityEngine;
int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
var prefix = typeof(ScalaEditing).GetNestedType("Key", BindingFlags.NonPublic)!.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
bool Dispatch(CodeWindow window, Event evt)
{
    bool native = (bool)prefix.Invoke(null, new object[] { window.CodeInput, evt })!;
    if (native)
    {
        // Mirrors the verified Utils.KeyPressed text boundary: a Return keycode
        // with character=0 inserts nothing; only its text event inserts a line.
        char typed = evt.character is '\r' or (char)3 ? '\n' : evt.character;
        if (typed != '\0' && (evt.modifiers & (EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt)) == 0)
        {
            var input = window.CodeInput;
            int at = input.stringPosition;
            input.text = input.text.Insert(at, typed.ToString());
            input.stringPosition = at + 1;
        }
    }
    return native;
}
var plain = new CodeWindow("val x = 1");
Check(Dispatch(plain, new Event { keyCode = KeyCode.Return, character = '\0' }), "Return key-only event stays native and inserts nothing");
Check(plain.CodeInput.text == "val x = 1", "key-only Return did not edit the source");
var enter = new Event { keyCode = KeyCode.Return, character = '\n' };
Check(!Dispatch(plain, enter) && enter.Used, "newline text event transformed once and consumed");
Check(plain.CodeInput.text == "val x = 1\n", "physical Return key+text sequence inserts one newline");
Check(plain.workspace.undoHistory.Count == 1, "one Enter records one atomic undo operation");
plain.workspace.undoHistory.Undo();
Check(plain.CodeInput.text == "val x = 1", "native Undo restores source after Enter");
plain.workspace.undoHistory.Redo();
Check(plain.CodeInput.text == "val x = 1\n", "native Redo restores assisted Enter");
var documented = new CodeWindow("/** Existing summary. */\nclass Farmer(plan: Seq[Crop]):");
documented.CodeInput.stringPosition = 3;
Dispatch(documented, new Event { keyCode = KeyCode.Return });
Dispatch(documented, new Event { character = '\n' });
Check(documented.CodeInput.text == "/**\n * Existing summary.\n * @param plan \n */\nclass Farmer(plan: Seq[Crop]):", "native Enter expands an existing Scaladoc and documents constructor parameters");
Check(documented.workspace.undoHistory.Count == 1, "Scaladoc expansion records one atomic undo operation");
documented.workspace.undoHistory.Undo();
Check(documented.CodeInput.text == "/** Existing summary. */\nclass Farmer(plan: Seq[Crop]):", "Undo restores the entire original inline Scaladoc");
documented.workspace.undoHistory.Redo();
Check(documented.CodeInput.text.Contains(" * Existing summary.\n * @param plan \n */", StringComparison.Ordinal), "Redo restores the preserved description and generated parameter tag");
var block = new CodeWindow("{}");
block.CodeInput.stringPosition = 1;
Dispatch(block, new Event { keyCode = KeyCode.Return });
Dispatch(block, new Event { character = '\r' });
Check(block.CodeInput.text == "{\n  \n}", "key-only plus CR text event expands a braced block once");
var pair = new CodeWindow();
var opening = new Event { character = '[' };
Dispatch(pair, opening);
Check(pair.CodeInput.text == "[]" && pair.CodeInput.stringPosition == 1 && opening.Used, "native hook pairs brackets and positions cursor");
Dispatch(pair, new Event { character = ']' });
Check(pair.CodeInput.text == "[]" && pair.CodeInput.stringPosition == 2, "managed closer consumes key without duplicate");
var doc = new CodeWindow("/** ");
Dispatch(doc, new Event { character = '[' });
Check(doc.CodeInput.text == "/** []" && doc.CodeInput.stringPosition == 5, "native hook pairs square brackets within Scaladoc");
var comment = new CodeWindow("one\ntwo");
comment.CodeInput.selectionStringAnchorPosition = 0;
var toggle = new Event { keyCode = KeyCode.Slash, modifiers = EventModifiers.Control };
Dispatch(comment, toggle);
Check(comment.CodeInput.text == "// one\n// two" && toggle.Used, "Ctrl slash comments native selection atomically");
Dispatch(comment, new Event { character = '/', modifiers = EventModifiers.Control });
Check(comment.CodeInput.text == "// one\n// two", "shortcut text echo cannot toggle comments a second time");
Dispatch(comment, new Event { keyCode = KeyCode.Slash, modifiers = EventModifiers.Command });
Check(comment.CodeInput.text == "one\ntwo", "Command slash uncomments native selection");
comment.workspace.undoHistory.Undo();
Check(comment.CodeInput.text == "// one\n// two", "comment toggle participates in native Undo");
var format = new Event { keyCode = KeyCode.L, modifiers = EventModifiers.Control | EventModifiers.Alt };
Dispatch(comment, format);
Check(ScalaEditorTools.Requests == 1 && format.Used, "format shortcut reaches editor request through native event");
var readOnly = new CodeWindow { CodeInput = { readOnly = true } };
Check(Dispatch(readOnly, new Event { character = '(' }), "read-only source bypasses smart-edit hook");
var composition = new CodeWindow();
Input.compositionString = "composing";
Check(Dispatch(composition, new Event { character = '(' }), "IME composition bypasses smart-edit hook");
Input.compositionString = "";
Check(FarmerScala.Plugin.Instance!.Logs.Count == 0, "native-hook adapter covers normal paths without exceptions");
var closeProbe = new NativeCompleter();
bool unownedFailure = false;
try { closeProbe.Close(); } catch (NullReferenceException) { unownedFailure = true; }
Check(unownedFailure, "native popup adapter reproduces Close-before-first-Open null owner");
var firstEdit = new CodeWindow();
Dispatch(firstEdit, new Event { character = '(' });
Check(firstEdit.CodeInput.text == "()" && FarmerScala.Plugin.Instance.Logs.Count == 0, "first assisted edit never closes an unowned native popup");
var inserted = typeof(ScalaEditing).GetNestedType("Inserted", BindingFlags.NonPublic)!.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)!;
Check(!(bool)inserted.Invoke(null, new object[] { new CodeWindow("/** ."), "." })! && FarmerScala.Plugin.Instance.Logs.Count == 0,
    "comment suppression before first completion avoids native null owner");
var failure = new CodeWindow { CodeInput = { ThrowDuringLabel = true } };
var failureEvent = new Event { character = '\n' };
Check(!Dispatch(failure, failureEvent) && failure.CodeInput.text == "\n" && failureEvent.Used, "native callback failure after mutation never replays the newline");
Check(FarmerScala.Plugin.Instance.Logs.Single().Contains("injected native mesh failure", StringComparison.Ordinal), "native fallback retains diagnostic stack context");
Console.WriteLine($"{checks} native editing hook checks passed (Unity/native surfaces adapted).");

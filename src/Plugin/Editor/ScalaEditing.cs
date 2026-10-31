using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Smart edits use the native UndoHistory and the native input field's text update path.</summary>
internal static class ScalaEditing
{
    private static readonly ConditionalWeakTable<CodeWindow, ScalaEditingSession> sessions = new ConditionalWeakTable<CodeWindow, ScalaEditingSession>();
    private static readonly FieldInfo UndoState = AccessTools.Field(typeof(CodeWindow), "undoState");
    private static readonly FieldInfo UndoTime = AccessTools.Field(typeof(CodeWindow), "undoChangeTime");
    private static readonly FieldInfo PreventUndo = AccessTools.Field(typeof(CodeWindow), "undoPreventNextChange");
    private static readonly MethodInfo UpdateTokens = AccessTools.Method(typeof(CodeWindow), "UpdateTokens");
    private static readonly MethodInfo NativeRestore = AccessTools.Method(typeof(CodeWindow), "SetToCodeState");
    private static bool reported;
    private sealed class State
    {
        internal string Source = ""; internal int Anchor, Focus;
    }

    internal static void ApplyAtomic(CodeWindow window, string next, int cursor)
    {
        Apply(window, new ScalaEditingResult { Source = next, Anchor = cursor, Focus = cursor });
        if (window != null)
            sessions.GetOrCreateValue(window).Reset(window.CodeInput.text);
    }

    private static void Apply(CodeWindow window, ScalaEditingResult edit)
    {
        if (window == null || window.CodeInput.readOnly || !EditorIntegration.IsScalaWindow(window))
            return;
        if (OptionHolder.GetString("tabs to spaces") == "enabled")
            edit = ScalaWhitespace.ExpandTabs(edit.Source, edit.Anchor, edit.Focus);
        var input = window.CodeInput;
        if (input.text == edit.Source)
        {
            Position(input, edit.Anchor, edit.Focus);
            return;
        }
        State Capture() => new State { Source = input.text, Anchor = input.selectionStringAnchorPosition, Focus = input.selectionStringFocusPosition };
        object Restore(object saved)
        {
            var reverse = Capture();
            var state = (State)saved;
            PreventUndo.SetValue(window, true);
            input.text = state.Source;
            Position(input, state.Anchor, state.Focus);
            UpdateTokens.Invoke(window, null);
            PreventUndo.SetValue(window, false);
            UndoState.SetValue(window, state.Source);
            UndoTime.SetValue(window, Time.time - 1f);
            sessions.GetOrCreateValue(window).Reset(state.Source);
            return reverse;
        }
        // Flush native typing grouped before this operation, then record the
        // whole assisted edit once. A custom edit never splits into brace,
        // newline and indentation entries in the game's undo stack.
        string pending = (string?)UndoState.GetValue(window) ?? input.text;
        if (pending != input.text)
        {
            var native = (Func<object, object>)Delegate.CreateDelegate(typeof(Func<object, object>), window, NativeRestore);
            window.workspace.undoHistory.AddChange(native, pending, window.gameObject);
        }
        window.workspace.undoHistory.AddChange(Restore, Capture(), window.gameObject);
        PreventUndo.SetValue(window, true);
        input.text = edit.Source;
        Position(input, edit.Anchor, edit.Focus);
        UpdateTokens.Invoke(window, null);
        PreventUndo.SetValue(window, false);
        UndoState.SetValue(window, edit.Source);
        // The next ordinary key starts its own native typing group rather
        // than merging into an immediately preceding scaffold/format action.
        UndoTime.SetValue(window, Time.time - 1f);
        if (window.workspace.codeCompleter.IsOpen)
            window.workspace.codeCompleter.Close();
        window.workspace.tooltip?.CloseTooltip();
    }

    private static void Position(CodeInputField input, int anchor, int focus)
    {
        input.stringPosition = Math.Max(0, Math.Min(input.text.Length, focus));
        input.selectionStringAnchorPosition = Math.Max(0, Math.Min(input.text.Length, anchor));
        input.selectionStringFocusPosition = Math.Max(0, Math.Min(input.text.Length, focus));
        input.ForceLabelUpdate();
    }

    private static void Report(Exception error)
    {
        if (reported)
            return;
        reported = true;
        Plugin.Instance?.LogInfo("Scala smart editing used native fallback: " + error);
    }

    [HarmonyPatch(typeof(CodeInputField), "KeyPressed")]
    private static class Key
    {
        private static bool Prefix(CodeInputField __instance, Event evt)
        {
            string source = __instance.text;
            int initialCursor = __instance.stringPosition;
            try
            {
                var window = __instance.GetComponentInParent<CodeWindow>();
                if (window == null || __instance.readOnly || !EditorIntegration.IsScalaWindow(window) ||
                    !__instance.isFocused || !string.IsNullOrEmpty(Input.compositionString))
                    return true;
                bool command = (evt.modifiers & (EventModifiers.Control | EventModifiers.Command)) != 0;
                bool alt = (evt.modifiers & EventModifiers.Alt) != 0;
                if (command && alt && evt.keyCode == KeyCode.L)
                {
                    ScalaEditorTools.RequestFormat(window);
                    evt.Use();
                    return false;
                }
                if (command && !alt && evt.keyCode == KeyCode.Slash)
                {
                    Apply(window, ScalaEditingSession.ToggleLineComment(source, __instance.selectionStringAnchorPosition, __instance.selectionStringFocusPosition));
                    sessions.GetOrCreateValue(window).Reset(__instance.text);
                    evt.Use();
                    return false;
                }
                if (command || alt)
                    return true;
                // Native Utils.KeyPressed inserts the text event's character,
                // not a Return keycode by itself. A platform may deliver both a
                // key-only event and a newline text event for one physical key.
                bool enterKey = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
                bool enter = evt.character == '\r' || evt.character == '\n' || evt.character == (char)3;
                if (enterKey && !enter)
                    return true;
                if (enter && window.workspace.codeCompleter.IsOpen && window.workspace.codeCompleter.IsMatch)
                    return true;
                char typed = enter ? '\n' : evt.character;
                bool backspace = evt.keyCode == KeyCode.Backspace;
                if (typed != '\n' && typed != '\r' && "()[]{}".IndexOf(typed) < 0 && !backspace)
                    return true;
                var session = sessions.GetOrCreateValue(window);
                if (!session.TryEdit(__instance.text, __instance.selectionStringAnchorPosition,
                    __instance.selectionStringFocusPosition, typed, backspace, out var edit))
                    return true;
                Apply(window, edit);
                evt.Use();
                // EditState.Continue is the enum's default value. Skipping the
                // original keeps Unity's input processing loop active.
                return false;
            }
            catch (Exception error)
            {
                Report(error);
                // A native callback may fail after accepting the transformed
                // text. Do not replay the key and insert a second delimiter.
                bool untouched = __instance.text == source && __instance.stringPosition == initialCursor;
                if (!untouched)
                    evt.Use();
                return untouched;
            }
        }
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.Load))]
    private static class Loaded
    {
        private static void Postfix(CodeWindow __instance) => sessions.Remove(__instance);
    }

    [HarmonyPatch(typeof(CodeWindow), "OnCodeInserted")]
    private static class Inserted
    {
        private static bool Prefix(CodeWindow __instance, string inserted)
        {
            if (!EditorIntegration.IsScalaWindow(__instance) || __instance.CodeInput.readOnly)
                return true;
            try
            {
                int cursor = __instance.CodeInput.stringPosition;
                // Native character-triggered completion has no Scala context
                // check, and native paste indentation assumes Python ':'.
                if (inserted.Length != 1 || !ScalaEditingContext.AllowsCompletion(__instance.CodeInput.text, cursor))
                {
                    if (__instance.workspace.codeCompleter.IsOpen)
                        __instance.workspace.codeCompleter.Close();
                    __instance.workspace.tooltip?.CloseTooltip();
                    UpdateTokens.Invoke(__instance, null);
                    return false;
                }
            }
            catch (Exception error) { Report(error); }
            return true;
        }
    }
}

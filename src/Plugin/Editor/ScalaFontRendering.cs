using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FarmerScala.Editor;

/// <summary>Uses private materials at the selected brightness for Scala code; never edits the game's shared font asset.</summary>
internal static class ScalaFontRendering
{
    internal const float Brightness = 1.5f;
    private const string CodeFontName = "FiraCode-Regular SDF";
    // TMP hashes font/material tag values after converting them to uppercase.
    internal const string HoverMaterialName = "FARMER_SCALA_CODE";
    private const string NativeFontTag = "<font=\"" + CodeFontName + "\">";
    internal const string CodeFontTag = "<font=\"" + CodeFontName + "\" material=\"" + HoverMaterialName + "\">";
    internal static readonly int FaceColor = Shader.PropertyToID("_FaceColor");
    private static readonly FieldInfo CodeText = AccessTools.Field(typeof(CodeWindow), "codeText");
    private static readonly HashSet<ScalaCodeTextMaterial> owners = new HashSet<ScalaCodeTextMaterial>();
    private static bool reportedFailure;

    internal static Material CreateCodeMaterial(Material source, string name)
    {
        var material = new Material(source) { name = name, hideFlags = HideFlags.HideAndDontSave };
        Color face = source.GetColor(FaceColor);
        material.SetColor(FaceColor, new Color(Brightness, Brightness, Brightness, face.a));
        return material;
    }

    internal static bool Refresh(CodeWindow window)
    {
        try
        {
            if (window == null || !(CodeText.GetValue(window) is TMP_Text text))
                return false;
            var owner = text.GetComponent<ScalaCodeTextMaterial>();
            if (!EditorIntegration.IsScalaWindow(window))
            {
                owner?.Restore();
                return false;
            }
            if (owner == null)
                owner = text.gameObject.AddComponent<ScalaCodeTextMaterial>();
            owners.Add(owner);
            owner.Apply(text);
            return EnsureHoverMaterial(text.font);
        }
        catch (Exception error) { Report(error); return false; }
    }

    private static bool EnsureHoverMaterial(TMP_FontAsset font)
    {
        int materialHash = TMP_TextUtilities.GetSimpleHashCode(HoverMaterialName);
        if (MaterialReferenceManager.TryGetMaterial(materialHash, out var existing))
            return existing != null;
        if (font == null || font.name != CodeFontName)
        {
            int fontHash = TMP_TextUtilities.GetSimpleHashCode(CodeFontName.ToUpperInvariant());
            if (!MaterialReferenceManager.TryGetFontAsset(fontHash, out font) || font == null)
                return false;
        }
        var source = font.material;
        if (source == null || !source.HasProperty(FaceColor))
            return false;
        // Rich font tags select a submesh's own material, not the parent text's.
        // Register one atlas-compatible material for those Scala hover snippets.
        MaterialReferenceManager.AddFontAsset(font);
        MaterialReferenceManager.AddFontMaterial(materialHash, CreateCodeMaterial(source, HoverMaterialName));
        return true;
    }

    internal static Material? CodeMaterial(TMP_FontAsset font)
    {
        if (!EnsureHoverMaterial(font)) return null;
        MaterialReferenceManager.TryGetMaterial(TMP_TextUtilities.GetSimpleHashCode(HoverMaterialName), out var material);
        return material;
    }

    internal static TooltipInfo? PrepareHover(CodeWindow window, TooltipInfo? info)
    {
        if (info == null || string.IsNullOrEmpty(info.text) || !EditorIntegration.IsScalaWindow(window) ||
            !info.text.Contains(NativeFontTag) || !Refresh(window))
            return info;
        // Native tooltip results can be cached. Copy their presentation metadata
        // instead of changing an object a Default save may use later.
        return new TooltipInfo(info.text.Replace(NativeFontTag, CodeFontTag), info.delay, info.fixedPosition, info.anchor, info.docs)
        {
            itemBlock = info.itemBlock
        };
    }

    internal static void Forget(ScalaCodeTextMaterial owner) => owners.Remove(owner);

    internal static void Clear()
    {
        foreach (var owner in owners.ToArray())
            if (owner != null)
                try { owner.Restore(); }
                catch (Exception error) { Report(error); }
        owners.Clear();
        // TMP owns its named-material cache for the application lifetime. Keep
        // that single material alive for tooltips already containing its tag;
        // destroying it would leave TMP's global lookup holding a dead entry.
    }

    private static void Report(Exception error)
    {
        if (reportedFailure)
            return;
        reportedFailure = true;
        Plugin.Instance?.LogInfo("Scala font rendering kept native fallback: " + error.Message);
    }

    [HarmonyPatch]
    private static class CodeWindowMaterials
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CodeWindow), "UpdateTokens");
            yield return AccessTools.Method(typeof(CodeWindow), "OnThemeChanged");
            yield return AccessTools.Method(typeof(CodeWindow), nameof(CodeWindow.UpdateSearch));
        }
        private static void Postfix(CodeWindow __instance) => Refresh(__instance);
    }

    [HarmonyPatch(typeof(CodeWindow), nameof(CodeWindow.GetTooltipInfo))]
    private static class HoverMaterials
    {
        private static void Postfix(CodeWindow __instance, ref TooltipInfo __result)
        {
            try { __result = PrepareHover(__instance, __result)!; }
            catch (Exception error) { Report(error); }
        }
    }
}

/// <summary>Owns only the visible code overlay's material; the input and title are untouched.</summary>
internal sealed class ScalaCodeTextMaterial : MonoBehaviour
{
    private TMP_Text? text;
    private Material? original;
    private Material? owned;

    internal void Apply(TMP_Text target)
    {
        if (owned != null && target.fontSharedMaterial == owned)
            return;
        Restore();
        var source = target.fontSharedMaterial;
        if (source == null || !source.HasProperty(ScalaFontRendering.FaceColor))
            return;
        text = target;
        original = source;
        owned = ScalaFontRendering.CreateCodeMaterial(source, "Farmer Scala code");
        target.fontSharedMaterial = owned;
    }

    internal void Restore()
    {
        if (text != null && owned != null && original != null && text.fontSharedMaterial == owned)
            text.fontSharedMaterial = original;
        if (owned != null)
            UnityEngine.Object.Destroy(owned);
        text = null;
        original = null;
        owned = null;
    }

    private void OnDestroy()
    {
        Restore();
        ScalaFontRendering.Forget(this);
    }
}

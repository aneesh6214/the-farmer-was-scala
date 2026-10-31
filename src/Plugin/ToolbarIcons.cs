using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace FarmerScala;

/// <summary>Antialiased monochrome vector renders, tinted by the native button.</summary>
internal static class ToolbarIcons
{
    internal static Sprite Load(Image native, string resource, string name)
    {
        using var stream = typeof(ToolbarIcons).Assembly.GetManifestResourceStream(resource)
            ?? throw new IOException("Missing toolbar icon: " + resource);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = name };
        if (!texture.LoadImage(bytes.ToArray()))
        {
            UnityEngine.Object.Destroy(texture);
            throw new IOException("Invalid toolbar icon: " + resource);
        }
        var original = native.sprite;
        // Retain fractional edge coverage from the vector render and sample it
        // smoothly at UI scale; a binary mask/nearest filtering loses that detail.
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.anisoLevel = 0;
        return UnityEngine.Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f),
            original != null ? original.pixelsPerUnit : 100f, 0, SpriteMeshType.FullRect);
    }

    internal static void Assign(Image target, Image native, Sprite sprite)
    {
        float side = Math.Min(native.rectTransform.rect.width, native.rectTransform.rect.height);
        if (side <= 0)
            side = 24;
        var rect = target.rectTransform;
        // Plus/New File icons may be made from a rotated cross. Custom glyphs
        // must not inherit that rotation, scale, or non-square image bounds.
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(side, side);
        target.sprite = sprite;
        target.overrideSprite = null;
        target.type = Image.Type.Simple;
        target.preserveAspect = true;
        target.material = native.material;
        target.color = native.color;
    }
}

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FarmerScala;

/// <summary>A filename adornment; hiding the native label also hides it during rename.</summary>
internal sealed class ScalaSaveBadge : MonoBehaviour
{
    private static Sprite? logo;
    private static readonly FieldInfo Icon = AccessTools.Field(typeof(ColoredButton), "icon");
    private TextMeshProUGUI label = null!;
    private Image? image;
    private Vector4 margin;
    private TextAlignmentOptions alignment;
    private bool scala;

    internal void Configure(TextMeshProUGUI text, bool isScala)
    {
        if (label == null)
        {
            label = text;
            margin = text.margin;
            alignment = text.alignment;
        }
        scala = isScala;
        if (scala && image == null)
        {
            var native = GetComponentsInChildren<ColoredButton>(true)
                .Select(button => Icon.GetValue(button) as Image).FirstOrDefault(icon => icon != null);
            if (native == null)
                native = Resources.FindObjectsOfTypeAll<Image>().First(icon => icon.sprite != null);
            if (logo == null)
                logo = ToolbarIcons.Load(native, "FarmerScala.ScalaLogo.png", "ScalaSaveLogo");
            image = new GameObject("ScalaSaveLogo", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
            image.transform.SetParent(label.transform, false);
            image.sprite = logo;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.material = native.material;
        }
        if (image != null)
            image.gameObject.SetActive(scala);
        Refresh();
    }

    private void LateUpdate()
    {
        if (label != null && scala)
            Refresh();
    }

    private void Refresh()
    {
        if (!scala)
        {
            label.margin = margin;
            label.alignment = alignment;
            return;
        }
        float side = Math.Min(label.fontSize * 1.25f, Math.Max(1f, label.rectTransform.rect.height - 8f));
        float gap = Math.Max(8f, side * .3f);
        var rect = image!.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0, .5f);
        rect.pivot = new Vector2(0, .5f);
        rect.localScale = Vector3.one;
        rect.sizeDelta = new Vector2(side, side);
        rect.anchoredPosition = new Vector2(margin.x + gap, 0);
        label.margin = new Vector4(margin.x + side + 2f * gap, margin.y, margin.z, margin.w);
        label.alignment = TextAlignmentOptions.MidlineLeft;
        image.color = label.color;
    }
}

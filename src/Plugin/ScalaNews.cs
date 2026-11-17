using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FarmerScala;

/// <summary>Uses the original title-page button and news-window prefabs.</summary>
internal static class ScalaNews
{
    internal const string Page = "farmer-scala:news";
    internal const string WindowName = "scala_mod_news";
    internal const string DemoVideo = "the-farmer-was-scala.mp4";
    internal const string DemoPoster = "the-farmer-was-scala.jpg";
    internal static string MediaDirectory => Path.Combine(Path.GetDirectoryName(typeof(ScalaNews).Assembly.Location)!, "assets");
    // Embed the root changelog so installed release notes work offline and match the build.
    internal static string Content
    {
        get
        {
            using var stream = typeof(ScalaNews).Assembly.GetManifestResourceStream("FarmerScala.Changelog.md")
                ?? throw new InvalidOperationException("The bundled Scala changelog is missing.");
            using var reader = new StreamReader(stream);
            string changelog = reader.ReadToEnd().Replace("\r\n", "\n");
            // Leave the repository changelog intact; nest its headings below
            // the mod introduction only in this in-game document.
            string body = Regex.Replace(changelog, @"(?m)^(#{1,5}) ", "#$1 ");
            return NewsMarkdown.Prepare("<meta windowType=side>\n\n# The Farmer Was Scala\n\n" +
                "The Farmer Was Replaced... by Scala!\n\n" +
                "![|1000x](video:" + DemoVideo + "|" + PosterUri + ")\n\n" +
                "Play [The Farmer Was Replaced](https://store.steampowered.com/app/2060160/The_Farmer_Was_Replaced/) " +
                "with [Scala 3](https://docs.scala-lang.org/scala3/book/introduction.html) in the game's own editor.\n\n" +
                "You can visit this window to see updates to the mod.\n\n" + body);
        }
    }

    // VideoUI's fallback loader has no base directory. Use an absolute file URI;
    // encode parentheses so the native Markdown destination is not cut short.
    private static string PosterUri => new Uri(Path.Combine(MediaDirectory, DemoPoster)).AbsoluteUri
        .Replace("(", "%28").Replace(")", "%29");

    private static Sprite? logo;
    private static readonly FieldInfo Icon = AccessTools.Field(typeof(ColoredButton), "icon");
    private static readonly FieldInfo Button = AccessTools.Field(typeof(ColoredButton), "button");
    private static readonly FieldInfo Title = AccessTools.Field(typeof(TitleWindow), "title");
    private static readonly FieldInfo Container = AccessTools.Field(typeof(TitleWindow), "container");
    private static readonly FieldInfo MarkdownPrefab = AccessTools.Field(typeof(TitleWindow), "markdownPrefab");
    private static readonly FieldInfo WorkspaceField = AccessTools.Field(typeof(TitleWindow), "titleWorkspace");
    private static readonly PropertyInfo Metadata = AccessTools.Property(typeof(TitleWindow), nameof(TitleWindow.Metadata));
    private static readonly PropertyInfo OpenMarkdown = AccessTools.Property(typeof(TitleWindow), nameof(TitleWindow.OpenMarkdownText));
    private static readonly MethodInfo LinkCalled = AccessTools.Method(typeof(TitleWindow), "LinkCalled");
    private static readonly MethodInfo PositionNews = AccessTools.Method(typeof(TitleWorkspace), "PositionNewWindow");
    private static readonly ConditionalWeakTable<TitleWindow, object> positioned = new ConditionalWeakTable<TitleWindow, object>();

    [HarmonyPatch(typeof(Menu), "Start")]
    private static class AddButton
    {
        private static void Postfix(Menu __instance)
        {
            GameObject? copy = null;
            try
            {
                var workspace = __instance.titleWorkspace;
                var original = workspace.openNewsButton;
                if (original == null || original.transform.parent.Find("ScalaModNewsButton") != null)
                    return;
                copy = UnityEngine.Object.Instantiate(original, original.transform.parent);
                copy.name = "ScalaModNewsButton";
                var colored = copy.GetComponentInChildren<ColoredButton>(true);
                if (colored == null)
                    throw new InvalidOperationException("Native news button has no ColoredButton.");
                var button = (UnityEngine.UI.Button)Button.GetValue(colored);
                // A fresh event also removes serialized listeners to official news.
                button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                button.onClick.AddListener(() => Open(workspace));
                colored.tooltipName = "Scala Changelog";
                colored.tooltipDescription = "Changes included in this version of the Scala mod.";
                var icon = (Image)Icon.GetValue(colored);
                var nativeIcon = (Image)Icon.GetValue(original.GetComponentInChildren<ColoredButton>(true));
                ToolbarIcons.Assign(icon, nativeIcon, Logo(nativeIcon));
                var anchor = copy.AddComponent<ScalaNewsAnchor>();
                anchor.Original = original.GetComponent<RectTransform>();
                anchor.DlcButtons = UnityEngine.Object.FindObjectsByType<DLCStatusButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                // The native row keeps ownership of its own buttons. Position
                // ours from their final bounds, even across different parents.
                var layout = copy.GetComponent<LayoutElement>() ?? copy.AddComponent<LayoutElement>();
                layout.ignoreLayout = true;
                copy.SetActive(true);
                anchor.Align();
                workspace.StartCoroutine(OpenOnFirstLaunch(workspace));
            }
            catch (Exception error)
            {
                if (copy != null)
                    UnityEngine.Object.Destroy(copy);
                Plugin.Instance?.LogError("Scala news button: " + error);
            }
        }
    }

    internal static bool ShouldOpenOnFirstLaunch(ISet<string>? history)
        => history == null || !history.Contains(WindowName);

    private static IEnumerator OpenOnFirstLaunch(TitleWorkspace workspace)
    {
        // Match native news startup: the menu and its canvas need one layout pass.
        yield return null;
        if (workspace != null && ShouldOpenOnFirstLaunch(workspace.windowHistory))
            Open(workspace);
    }

    private static Sprite Logo(Image native)
    {
        if (logo != null)
            return logo;
        logo = ToolbarIcons.Load(native, "FarmerScala.ScalaLogo.png", "ScalaModLogo");
        return logo;
    }

    private static void Open(TitleWorkspace workspace)
    {
        try
        {
            if (workspace.TryGetOpenWindow(WindowName, out var existing))
            {
                // Recover an existing blank page left by an earlier failed load.
                existing.GetComponent<TitleWindow>()?.LoadPage(Page);
                existing.SetMinmized(false);
                existing.transform.SetAsLastSibling();
                workspace.FitWindowsIntoContainer();
            }
            else
                workspace.OpenWindow(WindowName, Page);
        }
        catch (Exception error) { Plugin.Instance?.LogError("Scala news window: " + error); }
    }

    internal static bool IsModPage(string? page, string? windowName)
        => page == Page || windowName == WindowName &&
            (string.IsNullOrEmpty(page) || page == "__farmer_scala_empty__");

    [HarmonyPatch(typeof(TitleWindow), nameof(TitleWindow.LoadPage))]
    private static class LoadNews
    {
        private static bool Prefix(TitleWindow __instance, string __0)
        {
            if (!IsModPage(__0, __instance.GetComponent<Window>()?.windowName))
                return true;
            Load(__instance);
            return false;
        }
    }

    private static void Load(TitleWindow window)
    {
        if (!(window.GetComponent<Window>()?.workspace is TitleWorkspace workspace))
            return;
        // Menu restoration runs before the native news language is initialized.
        // Set up our embedded page directly, without touching NewsPath or news files.
        WorkspaceField.SetValue(window, workspace);
        window.openPage = Page;
        window.openLanguage = workspace.NewsLanguage;
        var metadata = new Dictionary<string, string>();
        Metadata.SetValue(window, metadata);
        var container = (RectTransform)Container.GetValue(window);
        for (int i = container.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(container.GetChild(i).gameObject);
        container.DetachChildren();
        container.anchoredPosition = Vector2.zero;
        var markdown = UnityEngine.Object.Instantiate((MarkdownText)MarkdownPrefab.GetValue(window), container);
        markdown.BasePath = MediaDirectory;
        var link = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), window, LinkCalled);
        markdown.Setup(Content, link, metadata);
        OpenMarkdown.SetValue(window, markdown);
        if (Title.GetValue(window) is TMP_Text title)
        {
            title.text = "";
            title.gameObject.SetActive(false);
        }
        if (!positioned.TryGetValue(window, out _))
        {
            positioned.Add(window, new object());
            window.StartCoroutine(PositionAfterLayout(window, workspace));
        }
    }

    private static IEnumerator PositionAfterLayout(TitleWindow window, TitleWorkspace workspace)
    {
        // A restored window loads during Menu.Start, before canvas sizing is final.
        // Button opens use this same native placement once; later resizes stay intact.
        yield return null;
        if (window == null || workspace == null)
            yield break;
        Canvas.ForceUpdateCanvases();
        PositionNews.Invoke(workspace, new object[] { window });
        window.GetComponent<Window>()?.UpdateSize(); // Preserve a restored minimized state.
        workspace.FitWindowsIntoContainer();
    }
}

internal sealed class ScalaNewsAnchor : MonoBehaviour
{
    internal RectTransform? Original;
    internal DLCStatusButton[] DlcButtons = Array.Empty<DLCStatusButton>();
    private readonly Vector3[] corners = new Vector3[4];
    private readonly List<(float Left, float Bottom, float Right, float Top)> occupied = new List<(float, float, float, float)>();

    internal void Align()
    {
        if (Original == null || !(transform.parent is RectTransform parent))
            return;
        var rect = (RectTransform)transform;
        rect.anchorMin = Original.anchorMin;
        rect.anchorMax = Original.anchorMax;
        rect.pivot = Original.pivot;
        rect.sizeDelta = Original.sizeDelta;
        rect.localScale = Original.localScale;
        rect.localRotation = Original.localRotation;
        var news = Bounds(Original, parent);
        if (news.Right <= news.Left)
            return; // Wait for the native canvas/layout to acquire its size.
        occupied.Clear();
        foreach (var button in DlcButtons)
            if (button != null && button.gameObject.activeInHierarchy && button.transform is RectTransform target)
                occupied.Add(Bounds(target, parent));
        var group = Original.parent.GetComponent<HorizontalOrVerticalLayoutGroup>();
        float gap = group != null && group.enabled ? Mathf.Max(0f, group.spacing) : Mathf.Max(8f, (news.Right - news.Left) * .2f);
        gap = NewsButtonPlacement.NativeGap(news, occupied, gap);
        rect.anchoredPosition = Original.anchoredPosition + new Vector2(NewsButtonPlacement.Offset(news, occupied, gap), 0f);
    }

    private (float Left, float Bottom, float Right, float Top) Bounds(RectTransform target, RectTransform parent)
    {
        target.GetWorldCorners(corners);
        Vector3 first = parent.InverseTransformPoint(corners[0]);
        float left = first.x, right = first.x, bottom = first.y, top = first.y;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 point = parent.InverseTransformPoint(corners[i]);
            left = Mathf.Min(left, point.x); right = Mathf.Max(right, point.x);
            bottom = Mathf.Min(bottom, point.y); top = Mathf.Max(top, point.y);
        }
        return (left, bottom, right, top);
    }

    private void LateUpdate() => Align();
}

using FarmerScala.Editor;
using TMPro;
using UnityEngine;
using System.Reflection;
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS " + description);
}
Check(ScalaLineNumberLayout.LineStarts("").SequenceEqual(new[] {0}), "empty file has line one");
Check(ScalaLineNumberLayout.LineStarts("a\r\nb\nc\r").SequenceEqual(new[] {0,3,5,7}), "mixed newline styles and final blank line");
var starts = ScalaLineNumberLayout.LineStarts("😀\nval x = 1\n");
Check(starts.SequenceEqual(new[] {0,3,13}), "source offsets use UTF-16 including supplementary characters");
Check(ScalaLineNumberLayout.LineAt(starts,2) == 1 && ScalaLineNumberLayout.LineAt(starts,3) == 2 && ScalaLineNumberLayout.LineAt(starts,13) == 3, "offset boundaries select logical source lines");
Check(new[] {1,99,100,999,1000}.Select(ScalaLineNumberLayout.Digits).SequenceEqual(new[] {2,2,3,3,4}), "gutter digit transitions");
var marked = new HashSet<int> {1};
Check(ScalaLineNumberLayout.ShowNumber(1,marked) && !ScalaLineNumberLayout.ShowNumber(2,marked) && !ScalaLineNumberLayout.ShowNumber(0,marked), "native zero-based breakpoint replaces exactly its number; wraps stay unnumbered");
var manyRows = Enumerable.Range(1,100000).Select(i => new ScalaNumberRow(i,-i*20,-i*20+16,-i*20-4)).ToArray();
Check(ScalaLineNumberLayout.FirstVisible(manyRows,-1000000) == 49999 && ScalaLineNumberLayout.FirstVisible(manyRows,0) == 0 && ScalaLineNumberLayout.FirstVisible(manyRows,-3000000) == manyRows.Length, "large-file viewport binary lookup includes partial rows and handles bounds");
var window = new GameObject().AddComponent<CodeWindow>();
void WindowUpdate(CodeWindow target) => typeof(ScalaLineNumbers).GetNestedType("WindowUpdate",BindingFlags.NonPublic)!
    .GetMethod("Postfix",BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,new object[] {target});
window.Scala = false;
WindowUpdate(window);
Check(window.GetComponent<ScalaLineNumberGutter>() == null, "Default windows do not receive a gutter");
var originalPanelSize = window.Panel.transform.sizeDelta;
var originalInputMargin = window.CodeInput.textComponent.margin;
var originalOverlayMargin = window.Overlay.margin;
var originalExtra = window.ExtraSize;
window.Scala = true;
WindowUpdate(window);
var gutter = window.GetComponent<ScalaLineNumberGutter>()!;
void Tick() => typeof(ScalaLineNumberGutter).GetMethod("LateUpdate",BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(gutter,null);
List<TextMeshProUGUI> Labels() => (List<TextMeshProUGUI>)typeof(ScalaLineNumberGutter).GetField("labels",BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gutter)!;
TextMeshProUGUI[] Visible() => Labels().Where(x => x.gameObject.Active).ToArray();
void Source(string value)
{
    window.CodeInput.text = value;
    var lines = ScalaLineNumberLayout.LineStarts(value);
    var chars = new List<TMP_CharacterInfo>();
    var visuals = new List<TMP_LineInfo>();
    for (int i = 0; i < lines.Length; i++)
    {
        // As TMP does, omit a final empty row; production must supply its number.
        if (lines[i] == value.Length) break;
        int first = chars.Count;
        chars.Add(new TMP_CharacterInfo {index = lines[i]});
        float baseline = -20 * (i+1);
        visuals.Add(new TMP_LineInfo { firstCharacterIndex = first,characterCount = 1,baseline = baseline,ascender = baseline+16,descender = baseline-4,lineHeight = 20 });
    }
    window.CodeInput.textComponent.textInfo = new TMP_TextInfo {characterCount = chars.Count,lineCount = visuals.Count,characterInfo = chars.ToArray(),lineInfo = visuals.ToArray()};
}
Source("one\n\nthree\n");
gutter.StartFor(window);
Tick();
Check(Visible().Select(x => x.text).SequenceEqual(new[] {"1","2","3","4"}), "production gutter numbers blank lines and a missing final TMP row");
Check(Visible().Select(x => x.rectTransform.anchoredPosition.y).SequenceEqual(new[] {-20f,-40f,-60f,-80f}), "numbers follow the rendered code baselines");
Check(window.Panel.transform.Children[0].Children.All(x => x.GetComponent<TextMeshProUGUI>()!.raycastTarget == false), "gutter leaves all native breakpoint pointer input available");
Check(window.Panel.transform.sizeDelta.x == originalPanelSize.x + 8 && window.Panel.transform.sizeDelta.y == originalPanelSize.y, "native gutter gains four UI units on each side without changing its height");
Check(window.CodeInput.textComponent.margin.x == originalInputMargin.x + 8 && window.Overlay.margin.x == originalOverlayMargin.x + 8 && window.ExtraSize.x == originalExtra.x + 8, "code/input move together and native sizing reserves the same gutter width");
Check(window.CodeInput.textComponent.margin.y == originalInputMargin.y && window.CodeInput.textComponent.margin.z == originalInputMargin.z && window.CodeInput.textComponent.margin.w == originalInputMargin.w, "vertical and right code margins remain unchanged");
Check(Visible().All(x => x.rectTransform.sizeDelta.x == originalPanelSize.x - 4), "extra gutter width goes into equal padding rather than enlarging the digits");
Check(Visible().All(x => x.alignment == TextAlignmentOptions.BaselineRight && x.rectTransform.anchorMin.x == 1f && x.rectTransform.pivot.x == 1f && x.rectTransform.anchoredPosition.x == -6), "all numbers share the same padded right edge and baseline alignment");
window.Panel.Toggle(1); Tick();
Check(Visible().Select(x => x.text).SequenceEqual(new[] {"1","3","4"}), "setting a breakpoint immediately hides only that line's number");
window.Panel.Toggle(1); Tick();
Check(Visible().Length == 4, "removing a breakpoint restores its number without a source edit");
gutter.StartFor(window);
Check(window.Panel.transform.sizeDelta.x == originalPanelSize.x + 8, "repeated updates do not accumulate gutter padding");
window.Panel.Toggle(0); Source("one"); window.Panel.Clear(); Tick();
Check(Visible().Single().text == "1", "native breakpoint clearing after editing restores numbers");
Source(""); Tick();
Check(Visible().Single().text == "1" && Visible()[0].rectTransform.anchoredPosition.y == -20, "empty document gets its first baseline from font ascent and margin");
Source(string.Join("\n",Enumerable.Repeat("x",175))); Tick();
float numberSize = Visible()[0].fontSize;
float threeDigitWidth = window.Panel.transform.sizeDelta.x;
Check(Visible()[0].text == "1" && numberSize == window.CodeInput.textComponent.fontSize * .8f, "single-digit numbers keep the normal font size in a three-digit document");
Check(threeDigitWidth >= 3 * numberSize * .6f + 12 && threeDigitWidth > originalPanelSize.x + 8, "gutter grows to fit three digits and both padding margins");
window.CodeInput.textComponent.rectTransform.anchoredPosition = new(0,620);
window.Panel.transform.anchoredPosition = new(0,620); Tick();
Check(Visible().Any(x => x.text == "32") && Visible().All(x => x.rectTransform.anchoredPosition.x == -6 && x.fontSize == numberSize), "32 uses the same right edge and font size as 1");
window.CodeInput.textComponent.rectTransform.anchoredPosition = new(0,3440);
window.Panel.transform.anchoredPosition = new(0,3440); Tick();
Check(Visible().Any(x => x.text == "175") && Visible().All(x => x.alignment == TextAlignmentOptions.BaselineRight && x.rectTransform.anchoredPosition.x == -6 && x.fontSize == numberSize), "175 aligns its last two digits with 32 and its last digit with 1");
window.CodeInput.textComponent.rectTransform.anchoredPosition = default;
window.Panel.transform.anchoredPosition = default;
Source(string.Join("\n",Enumerable.Repeat("x",2000))); Tick();
Check(window.Panel.transform.sizeDelta.x > threeDigitWidth && Visible().All(x => x.fontSize == numberSize), "four-digit line counts widen the gutter instead of shrinking numbers");
Check(window.CodeInput.textComponent.margin.x - originalInputMargin.x == window.Panel.transform.sizeDelta.x - originalPanelSize.x && window.Overlay.margin.x - originalOverlayMargin.x == window.ExtraSize.x - originalExtra.x, "dynamic gutter width is reserved consistently by input, overlay and native sizing");
Check(Visible().Length < 10 && Labels().Count < 10, "long documents allocate labels only for the visible viewport");
window.CodeInput.textComponent.rectTransform.anchoredPosition = new(0,10000);
window.Panel.transform.anchoredPosition = new(0,10000);
Tick();
Check(int.Parse(Visible()[0].text) == 500, "scrolling renders the partially visible first row at the new viewport");
int pooled = Labels().Count; Tick();
Check(Labels().Count == pooled, "unchanged frames reuse the label pool");
window.CodeInput.textComponent.rectTransform.anchoredPosition = default;
window.Panel.transform.anchoredPosition = default;
// Simulate an additional wrapped visual row of source line one.
Source("one\ntwo");
var info = window.CodeInput.textComponent.textInfo;
info.characterInfo = new[] {new TMP_CharacterInfo {index=0}, new TMP_CharacterInfo {index=1},new TMP_CharacterInfo {index=4}};
info.characterCount = 3;
info.lineInfo = new[] {
    new TMP_LineInfo {firstCharacterIndex=0,characterCount=1,baseline=-20,ascender=-4,descender=-24,lineHeight=20},
    new TMP_LineInfo {firstCharacterIndex=1,characterCount=1,baseline=-40,ascender=-24,descender=-44,lineHeight=20},
    new TMP_LineInfo {firstCharacterIndex=2,characterCount=1,baseline=-60,ascender=-44,descender=-64,lineHeight=20}};
info.lineCount = 3; Tick();
Check(Visible().Select(x => x.text).SequenceEqual(new[] {"1","2"}) && Visible()[1].rectTransform.anchoredPosition.y == -60, "wrapped continuations do not create extra source line numbers");
Source(string.Join("\n",Enumerable.Repeat("x",2000))); Tick();
var root = window.Panel.transform.Children.Last();
window.Scala = false; Tick();
Check(!root.gameObject.Active && root.gameObject.Destroyed && Labels().Count == 0, "switching to Default removes the gutter and its labels");
Check(window.CodeInput.text.Count(c => c == '\n') == 1999 && window.CodeInput.textComponent.margin == originalInputMargin && window.Overlay.margin == originalOverlayMargin && window.Panel.transform.sizeDelta == originalPanelSize && window.ExtraSize == originalExtra, "Default restores native gutter, text margins and window sizing without changing source");
window.Scala = true; Source("one\ntwo"); gutter.StartFor(window); Tick();
Check(Visible().Length == 2 && window.Panel.transform.sizeDelta.x == originalPanelSize.x + 8, "Scala window can be re-enabled without cumulative padding");
// A read-only library window uses exactly this same native CodeWindow type and
// Scala ownership predicate, with no dependency on editable project registration.
Check(typeof(ScalaLineNumbers).GetNestedType("WindowUpdate",BindingFlags.NonPublic) != null, "all native Scala CodeWindows share the update hook");
var library = new GameObject().AddComponent<CodeWindow>();
WindowUpdate(library);
Check(library.GetComponent<ScalaLineNumberGutter>() != null, "unregistered library CodeWindow receives the same gutter");
ScalaLineNumbers.Clear();
Check(Labels().Count == 0 && gutter.Destroyed && library.GetComponent<ScalaLineNumberGutter>() == null && window.Panel.transform.sizeDelta == originalPanelSize, "plugin cleanup removes labels and restores native gutter width");
Console.WriteLine($"Line-number checks passed: {checks}; production layout/lifecycle with Unity/TMP adapters.");

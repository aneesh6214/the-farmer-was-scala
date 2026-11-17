namespace FarmerScala.Editor;

/// <summary>All Scala source/hover syntax colors. Native window/search colors stay with the game theme.</summary>
internal sealed class ScalaSyntaxPalette
{
    internal const string ParenthesisBackground = "#A0A8B04D";
    // Reference: IntelliJ IDEA's editor scheme named "Dark", normal color vision,
    // with the Scala plugin enabled and rainbow/collection highlighting disabled.
    // The values below are our palette: keyword/string/documentation hues are adjusted
    // directly from that reference. There is no separate reference palette or
    // runtime override layer. Function calls and game symbols use native colors.
    // Resolve explicit attributes (including parents) before fallback keys, as
    // AbstractColorsScheme.getDirectlyDefinedAttributes / EditorColorsSchemeImpl do.
    // Pinned upstream references (JetBrains, Apache-2.0):
    // Dark: https://github.com/JetBrains/intellij-community/blob/b2b0ef4737a84d25d3a71441da0584ad60ec892c/platform/platform-resources/src/themes/expUI/expUI_darkScheme.xml
    // Parent: https://github.com/JetBrains/intellij-community/blob/b2b0ef4737a84d25d3a71441da0584ad60ec892c/platform/platform-resources/src/DefaultColorSchemesManager.xml
    // Scala categories: https://github.com/JetBrains/intellij-scala/blob/3f9b0191aa108bdf01b575e5ffbcfb87a8ec4850/scala/scala-impl/src/org/jetbrains/plugins/scala/highlighter/DefaultHighlighter.java
    // Scala Dark: https://github.com/JetBrains/intellij-scala/blob/3f9b0191aa108bdf01b575e5ffbcfb87a8ec4850/scala/scala-impl/resources/colorSchemes/expui/ScalaDark.xml
    // Scala parent: https://github.com/JetBrains/intellij-scala/blob/3f9b0191aa108bdf01b575e5ffbcfb87a8ec4850/scala/scala-impl/resources/colorSchemes/ScalaDarcula.xml
    // Java fallback keys: https://github.com/JetBrains/intellij-community/blob/b2b0ef4737a84d25d3a71441da0584ad60ec892c/java/java-frontback-psi-impl/src/com/intellij/ide/highlighter/JavaHighlightingColors.java
    internal const string ForegroundColor = "#BCBEC4";        // TEXT / DEFAULT_IDENTIFIER; classes, locals, parameters
    internal const string LineNumberColor = CommentColor;     // Muted gutter text shares the ordinary comment hue
    internal const string TypeColor = ForegroundColor;        // Scala Class/Object/Predefined types -> ordinary foreground
    internal const string KeywordColor = "#DF915E";           // DEFAULT_KEYWORD, adjusted hue (also booleans/null and valid escapes)
    internal const string StringColor = "#79A978";            // DEFAULT_STRING; sampled from user reference, 2026-10-31 (also characters)
    internal const string CommentColor = "#7A7E85";           // DEFAULT_LINE_COMMENT / DEFAULT_BLOCK_COMMENT
    internal const string DocCommentColor = "#445649";        // At 1.5x, approximates the reference's #66816D appearance at 1x
    internal const string DocTagColor = "#4E6B55";            // At 1.5x, approximates reference #75A17F (also @param labels)
    internal const string DocMarkupColor = DocTagColor;      // DEFAULT_DOC_MARKUP shares the lighter documentation hue
    internal const string NumberColor = "#2AACB8";            // DEFAULT_NUMBER
    internal const string FunctionDeclarationColor = "#56A8F5"; // DEFAULT_FUNCTION_DECLARATION
    internal const string NamedArgumentColor = "#56C1D6";     // Scala Named Argument, ScalaDark.xml
    internal const string FieldColor = "#C77DBB";             // DEFAULT_STATIC_FIELD; Scala template val/var and enum cases
    internal const string TypeParameterColor = "#4E807D";     // Scala Type parameter / Type Alias, inherited from Darcula
    internal const string InterpolationColor = "#00B8BB";     // Interpolated String Injection, inherited from ScalaDarcula
    internal const string AnnotationColor = "#B3AE60";        // DEFAULT_METADATA -> Scala Annotation name

    // Empty permits marker palettes in rendering tests to use their own foreground.
    public string Keyword = "", String = "", Comment = "", Builtin = "";
    public string FunctionDeclaration = "", FunctionCall = "", Field = FieldColor;
    public string Parameter = "", Local = "", TypeParameter = TypeParameterColor, NamedArgument = NamedArgumentColor;
    public string Class = TypeColor, Number = NumberColor, Foreground = "", Operator = "";
    public string Search = "", SelectedSearch = "", Documentation = DocTagColor, DocMarkup = DocMarkupColor;
    public string DocComment = "", Interpolation = "", Escape = "", Annotation = "";

    // One construction path for the editor and syntax-colored hover content.
    internal static ScalaSyntaxPalette Create(string builtin, string function, string search = "", string selectedSearch = "") => new ScalaSyntaxPalette
    {
        Keyword = KeywordColor, String = StringColor, Comment = CommentColor, Builtin = builtin,
        FunctionDeclaration = FunctionDeclarationColor, FunctionCall = function,
        Class = TypeColor, Number = NumberColor, Foreground = ForegroundColor, Operator = ForegroundColor,
        Parameter = ForegroundColor, Local = ForegroundColor, Field = FieldColor,
        TypeParameter = TypeParameterColor, NamedArgument = NamedArgumentColor,
        DocComment = DocCommentColor, Documentation = DocTagColor, DocMarkup = DocMarkupColor,
        Interpolation = InterpolationColor, Escape = KeywordColor, Annotation = AnnotationColor,
        Search = search, SelectedSearch = selectedSearch
    };

    internal string Symbol(string kind) => kind switch
    {
        "builtin" => Builtin,
        "class" or "object" => Class,
        "typeParameter" or "typeAlias" => TypeParameter,
        "functionDeclaration" => FunctionDeclaration,
        "function" => FunctionCall,
        "field" => Field,
        "parameter" => Parameter,
        "namedArgument" => NamedArgument,
        "annotation" => Annotation,
        "value" => Local,
        _ => Foreground
    };
}

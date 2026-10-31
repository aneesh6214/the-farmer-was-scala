using System;
using System.Collections.Generic;
using System.Linq;

namespace FarmerScala.Editor;

/// <summary>Conservative, tolerant Scala source index. It never compiles or executes player code.
/// Handles explicit declarations, simple constructor/alias types, direct imports and inheritance.
/// Overloads, implicits, extensions and generic inference require the compiler provider.</summary>
internal sealed class ScalaCompletionModel
{
    private sealed class Scope
    {
        public Scope? Parent;
        public Symbol? Owner;
        public int Start, End;
        public readonly List<Symbol> Names = new List<Symbol>();
        public readonly Dictionary<string, Symbol> ByName = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        public readonly List<Scope> Children = new List<Scope>();
    }
    private sealed class Symbol
    {
        public string Name = "", Kind = "", ColorKind = "", File = "", Signature = "", Documentation = "", Type = "";
        public string? Namespace;
        public int Offset;
        public Scope? Body;
        public Scope Scope = null!;
        public bool Member = true;
        public readonly List<string> Bases = new List<string>();
    }
    private sealed class File
    {
        public string Name = "", Source = "";
        public List<ScalaToken> Tokens = new List<ScalaToken>();
        public readonly Dictionary<int, int> Pairs = new Dictionary<int, int>();
        public readonly List<Scope> Scopes = new List<Scope>();
        public readonly Dictionary<string, Tuple<string, string>> Imports = new Dictionary<string, Tuple<string, string>>(StringComparer.Ordinal);
        public readonly List<string> Wildcards = new List<string>();
        public Scope Root = new Scope();
        public readonly Dictionary<int, Symbol> Declarations = new Dictionary<int, Symbol>();
    }
    private readonly Dictionary<string, File> files = new Dictionary<string, File>(StringComparer.Ordinal);
    private static readonly HashSet<string> TypeDeclarations = new HashSet<string>(new[] { "class", "trait", "enum", "object" }, StringComparer.Ordinal);

    public ScalaCompletionModel(IReadOnlyDictionary<string, string> sources)
    {
        foreach (var pair in sources)
        {
            var file = new File
            {
                Name = pair.Key,
                Source = pair.Value,
                Tokens = ScalaLexer.Lex(pair.Value).Where(t => t.Kind != ScalaTokenKind.Comment).ToList()
            };
            file.Root.End = pair.Value.Length + 1;
            file.Scopes.Add(file.Root);
            files[pair.Key] = file;
            PairBrackets(file);
        }
        foreach (var file in files.Values)
            Parse(file);
    }

    private static string Name(ScalaToken token) => token.Text.Trim('`');
    private static Scope At(File file, int offset)
    {
        Scope scope = file.Root;
        while (scope.Children.Count > 0)
        {
            int low = 0, high = scope.Children.Count - 1, found = -1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (scope.Children[middle].Start <= offset)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                    high = middle - 1;
            }
            if (found < 0 || scope.Children[found].End <= offset)
                break;
            scope = scope.Children[found];
        }
        return scope;
    }
    private static void Add(File file, Scope scope, Symbol symbol)
    {
        scope.Names.Add(symbol);
        if (!scope.ByName.ContainsKey(symbol.Name))
            scope.ByName[symbol.Name] = symbol;
        file.Declarations[symbol.Offset] = symbol;
    }
    private static void PairBrackets(File file)
    {
        var stack = new Stack<int>();
        for (int i = 0; i < file.Tokens.Count; i++)
        {
            string text = file.Tokens[i].Text;
            if (text == "(" || text == "[" || text == "{")
                stack.Push(i);
            else if ((text == ")" || text == "]" || text == "}") && stack.Count > 0)
            {
                int start = stack.Peek();
                string open = file.Tokens[start].Text;
                if (open == "(" && text == ")" || open == "[" && text == "]" || open == "{" && text == "}")
                {
                    stack.Pop();
                    file.Pairs[start] = i;
                    file.Pairs[i] = start;
                }
            }
        }
    }
    private void Parse(File file)
    {
        var tokens = file.Tokens;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != ScalaTokenKind.Keyword)
                continue;
            string word = tokens[i].Text;
            if (word == "import")
            {
                ReadImport(file, i);
                continue;
            }
            bool type = TypeDeclarations.Contains(word);
            if (!type && word != "def" && word != "val" && word != "var" && word != "type")
                continue;
            if (i + 1 >= tokens.Count || !tokens[i + 1].Identifier || tokens[i + 1].Text == "=")
                continue;
            var name = tokens[i + 1];
            var scope = At(file, name.Start);
            // Constructor `val x` and parameters are registered separately below.
            if (file.Declarations.ContainsKey(name.Start))
                continue;
            int headerEnd = HeaderEnd(file, i + 2);
            var symbol = new Symbol
            {
                Name = Name(name),
                ColorKind = word == "type" ? "typeAlias" : "",
                Offset = name.Start,
                File = file.Name,
                Scope = scope,
                Kind = type || word == "type" ? word == "object" ? "object" : "class" : word == "def" ? "function" : scope.Owner?.Kind == "class" || scope.Owner?.Kind == "object" ? "field" : "value"
            };
            int signatureEnd = headerEnd < tokens.Count ? tokens[headerEnd].Start : file.Source.Length;
            symbol.Signature = file.Source.Substring(tokens[i].Start, Math.Max(0, signatureEnd - tokens[i].Start)).Trim().TrimEnd(':', '=', '{', ' ');
            if (symbol.Signature.Length > 300)
                symbol.Signature = symbol.Signature.Substring(0, 300) + "…";
            symbol.Documentation = Documentation(file.Source, tokens[i].Start);
            symbol.Type = ReadType(file, i + 2, headerEnd, type ? symbol.Name : "");
            Add(file, scope, symbol);
            if (type || word == "def")
            {
                int end = BodyEnd(file, i, headerEnd);
                symbol.Body = new Scope { Start = name.End, End = end, Parent = scope, Owner = symbol };
                file.Scopes.Add(symbol.Body);
                scope.Children.Add(symbol.Body);
                ReadTypeParameters(file, symbol, i + 2, headerEnd);
                ReadParameters(file, symbol, i + 2, headerEnd, type && i > 0 && tokens[i - 1].Text == "case");
                if (type)
                    for (int b = i + 2; b + 1 < headerEnd; b++)
                        if ((tokens[b].Text == "extends" || tokens[b].Text == "with") && tokens[b + 1].Identifier)
                            symbol.Bases.Add(Name(tokens[b + 1]));
            }
        }
    }
    private static void ReadTypeParameters(File file, Symbol owner, int start, int end)
    {
        var tokens = file.Tokens;
        if (start >= tokens.Count || tokens[start].Text != "[")
            return;
        int limit = file.Pairs.TryGetValue(start, out int close) ? close : end;
        bool expectName = true;
        for (int i = start + 1; i < limit; i++)
        {
            var token = tokens[i];
            if (token.Text == "[")
            {
                if (file.Pairs.TryGetValue(i, out int nested))
                    i = nested;
                continue;
            }
            if (token.Text == ",")
            {
                expectName = true;
                continue;
            }
            if (!expectName || !token.Identifier || token.Kind == ScalaTokenKind.Operator)
                continue;
            var parameter = new Symbol
            {
                Name = Name(token),
                Kind = "typeParameter",
                File = file.Name,
                Offset = token.Start,
                Scope = owner.Body!,
                Member = false,
                Signature = "type " + Name(token)
            };
            Add(file, owner.Body!, parameter);
            expectName = false;
        }
    }
    private static int HeaderEnd(File file, int start)
    {
        for (int i = start; i < file.Tokens.Count; i++)
        {
            string text = file.Tokens[i].Text;
            if ((text == "(" || text == "[") && file.Pairs.TryGetValue(i, out int close))
            {
                i = close;
                continue;
            }
            if (text == "\n" || text == ";" || text == "=" || text == "{")
                return i;
        }
        return file.Tokens.Count;
    }
    private static int BodyEnd(File file, int declaration, int header)
    {
        var tokens = file.Tokens;
        int body = header;
        if (body < tokens.Count && tokens[body].Text == "=")
            body++;
        if (body < tokens.Count && tokens[body].Text == "{" && file.Pairs.TryGetValue(body, out int close))
            return tokens[close].End;
        int line = tokens[declaration].Line;
        int indent = tokens.First(t => t.Line == line).Column;
        int next = body;
        while (next < tokens.Count && tokens[next].Line == line)
            next++;
        while (next < tokens.Count && tokens[next].Kind == ScalaTokenKind.Newline)
            next++;
        if (next == tokens.Count)
            return file.Source.Length + 1;
        if (tokens[next].Column <= indent)
            return tokens[next].Start;
        for (int i = next + 1; i < tokens.Count; i++)
            if (tokens[i].Kind != ScalaTokenKind.Newline && tokens[i].Line > tokens[i - 1].Line && tokens[i].Column <= indent)
                return tokens[i].Start;
        return file.Source.Length + 1;
    }
    private static string ReadType(File file, int start, int end, string fallback)
    {
        var tokens = file.Tokens;
        string Qualified(int at)
        {
            string name = Name(tokens[at]);
            while (at + 2 < tokens.Count && tokens[at + 1].Text == "." && tokens[at + 2].Identifier)
            {
                name += "." + Name(tokens[at + 2]);
                at += 2;
            }
            return name;
        }
        for (int i = start; i < end && i < tokens.Count; i++)
        {
            if ((tokens[i].Text == "(" || tokens[i].Text == "[") && file.Pairs.TryGetValue(i, out int close))
            {
                i = close;
                continue;
            }
            if (tokens[i].Text == ":" && i + 1 < tokens.Count && tokens[i + 1].Identifier)
                return Qualified(i + 1);
        }
        if (end < tokens.Count && tokens[end].Text == "=")
        {
            int at = end + 1;
            if (at < tokens.Count && tokens[at].Text == "new")
                at++;
            if (at < tokens.Count && tokens[at].Identifier)
                return Qualified(at);
            if (at < tokens.Count && tokens[at].Kind == ScalaTokenKind.String)
                return "String";
            if (at < tokens.Count && tokens[at].Kind == ScalaTokenKind.Number)
                return "Double";
        }
        return fallback;
    }
    private static void ReadParameters(File file, Symbol symbol, int start, int end, bool caseClass)
    {
        var tokens = file.Tokens;
        for (int i = start; i + 2 < end; i++)
        {
            if (!tokens[i].Identifier || tokens[i + 1].Text != ":" || !tokens[i + 2].Identifier)
                continue;
            bool field = caseClass || i > 0 && (tokens[i - 1].Text == "val" || tokens[i - 1].Text == "var");
            var parameter = new Symbol
            {
                Name = Name(tokens[i]),
                Kind = field ? "field" : "parameter",
                Type = Name(tokens[i + 2]),
                File = file.Name,
                Offset = tokens[i].Start,
                Scope = symbol.Body!,
                Member = field,
                Signature = tokens[i].Text + ": " + tokens[i + 2].Text
            };
            Add(file, symbol.Body!, parameter);
        }
    }
    private void ReadImport(File file, int start)
    {
        var line = file.Tokens.Skip(start + 1).TakeWhile(t => t.Kind != ScalaTokenKind.Newline && t.Text != ";").ToList();
        if (line.Count == 0 || !line[0].Identifier)
            return;
        var chain = new List<string> { Name(line[0]) };
        int consumed = 1;
        while (consumed + 1 < line.Count && line[consumed].Text == "." && line[consumed + 1].Identifier && line[consumed + 1].Text != "_" && line[consumed + 1].Text != "*")
        {
            chain.Add(Name(line[consumed + 1]));
            consumed += 2;
        }
        int count = chain.Count;
        while (count > 0 && !ModuleExists(string.Join(".", chain.Take(count))))
            count--;
        if (count == 0)
            return;
        string module = string.Join(".", chain.Take(count));
        if (count == chain.Count && (consumed == line.Count || line[consumed].Text == "as" || line[consumed].Text == "=>"))
        {
            string alias = consumed + 1 < line.Count ? Name(line[consumed + 1]) : chain[chain.Count - 1];
            file.Imports[alias] = Tuple.Create(module, "");
            return;
        }
        int memberAt = count * 2;
        for (int i = memberAt; i < line.Count; i++)
        {
            if (line[i].Text == "*" || line[i].Text == "_")
            {
                file.Wildcards.Add(module);
                continue;
            }
            if (!line[i].Identifier)
                continue;
            string original = Name(line[i]), alias = original;
            if (i + 2 < line.Count && (line[i + 1].Text == "as" || line[i + 1].Text == "=>"))
            {
                alias = Name(line[i + 2]);
                i += 2;
            }
            file.Imports[alias] = ModuleExists(module + "." + original) ? Tuple.Create(module + "." + original, "") : Tuple.Create(module, original);
        }
    }
    private bool ModuleExists(string name) => files.ContainsKey(name) || files.Keys.Any(n => n.StartsWith(name + ".", StringComparison.Ordinal));
    private Symbol? Module(string name)
    {
        if (files.TryGetValue(name, out var file))
            return new Symbol { Name = name.Substring(name.LastIndexOf('.') + 1), Kind = "object", File = name, Offset = 0, Body = file.Root, Scope = file.Root, Signature = "object " + name };
        var first = files.Keys.Where(n => n.StartsWith(name + ".", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        if (first == null)
            return null;
        return new Symbol
        {
            Name = name.Substring(name.LastIndexOf('.') + 1),
            Kind = "object",
            Namespace = name,
            File = first,
            Offset = 0,
            Scope = files[first].Root,
            Signature = "package " + name
        };
    }
    private IEnumerable<Symbol> Exports(string module)
    {
        if (files.TryGetValue(module, out var file))
            return file.Root.Names;
        string prefix = module.Length == 0 ? "" : module + ".";
        return files.Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => n.Substring(prefix.Length).Split('.')[0])
            .Distinct(StringComparer.Ordinal).Select(n => Module(prefix + n)).Where(s => s != null).Cast<Symbol>();
    }
    private static string Documentation(string source, int start)
    {
        string prefix = source.Substring(0, start).TrimEnd();
        if (!prefix.EndsWith("*/", StringComparison.Ordinal))
            return "";
        int open = prefix.LastIndexOf("/**", StringComparison.Ordinal);
        if (open < 0)
            return "";
        string body = prefix.Substring(open + 3, prefix.Length - open - 5);
        return string.Join("\n", body.Split('\n').Select(l => l.Trim().TrimStart('*').TrimStart())).Trim();
    }
    private Symbol? Lookup(File file, Scope scope, string name, int depth = 0)
    {
        if (depth > 16)
            return null;
        if (name.Contains('.'))
        {
            var path = name.Split('.');
            var symbol = Lookup(file, scope, path[0], depth + 1);
            for (int i = 1; i < path.Length && symbol != null; i++)
            {
                var type = ValueType(files[symbol.File], symbol, depth + 1);
                symbol = type == null ? null : Members(files[type.File], type, new HashSet<Symbol>()).FirstOrDefault(s => s.Name == path[i]);
            }
            return symbol;
        }
        for (var current = scope; current != null; current = current.Parent)
        {
            if (current.ByName.TryGetValue(name, out var local))
                return local;
            if (current.Owner != null)
            {
                var inherited = Members(file, current.Owner, new HashSet<Symbol>()).FirstOrDefault(s => s.Name == name);
                if (inherited != null)
                    return inherited;
            }
        }
        if (file.Imports.TryGetValue(name, out var imported))
            return imported.Item2.Length == 0 ? Module(imported.Item1)
            : files.TryGetValue(imported.Item1, out var importedFile) && importedFile.Root.ByName.TryGetValue(imported.Item2, out var importedSymbol) ? importedSymbol : null;
        foreach (string module in file.Wildcards)
        {
            var symbol = Exports(module).FirstOrDefault(s => s.Name == name);
            if (symbol != null)
                return symbol;
        }
        int lastDot = file.Name.LastIndexOf('.');
        return lastDot >= 0 ? Module(file.Name.Substring(0, lastDot + 1) + name) ?? Module(name) : Module(name);
    }
    private IEnumerable<Symbol> Members(File file, Symbol type, HashSet<Symbol> visited)
    {
        if (!visited.Add(type))
            yield break;
        if (type.Namespace != null)
        {
            foreach (var symbol in Exports(type.Namespace))
                yield return symbol;
            yield break;
        }
        if (type.Body != null)
            foreach (var member in type.Body.Names)
                if (member.Member)
                    yield return member;
        foreach (string parent in type.Bases)
        {
            var baseType = Lookup(files[type.File], type.Scope, parent, 1);
            if (baseType != null)
                foreach (var member in Members(files[baseType.File], baseType, visited))
                    yield return member;
        }
    }
    private Symbol? ValueType(File file, Symbol symbol, int depth)
    {
        if (symbol.Kind == "class" || symbol.Kind == "object")
            return symbol;
        if (depth > 16 || symbol.Type.Length == 0 || symbol.Type == symbol.Name)
            return null;
        var resolved = Lookup(files[symbol.File], symbol.Scope, symbol.Type, depth + 1);
        return resolved == null ? null : ValueType(file, resolved, depth + 1);
    }
    private Symbol? Expression(File file, int index, int depth = 0)
    {
        if (depth > 16 || index < 0)
            return null;
        var tokens = file.Tokens;
        if (tokens[index].Text == ")" && file.Pairs.TryGetValue(index, out int open))
            return Expression(file, open - 1, depth + 1);
        if (!tokens[index].Identifier && tokens[index].Text != "this")
            return null;
        if (index >= 2 && tokens[index - 1].Text == ".")
        {
            var receiver = Expression(file, index - 2, depth + 1);
            var type = receiver == null ? null : ValueType(file, receiver, depth + 1);
            return type == null ? null : Members(files[type.File], type, new HashSet<Symbol>()).FirstOrDefault(s => s.Name == Name(tokens[index]));
        }
        var scope = At(file, tokens[index].Start);
        if (tokens[index].Text == "this")
        {
            for (var current = scope; current != null; current = current.Parent)
                if (current.Owner?.Kind == "class" || current.Owner?.Kind == "object")
                    return current.Owner;
        }
        return Lookup(file, scope, Name(tokens[index]));
    }
    public AnalysisResolution Resolve(string fileName, int offset)
    {
        var result = new AnalysisResolution();
        if (!files.TryGetValue(fileName, out var file))
            return result;
        int index = file.Tokens.FindIndex(t => t.Start < offset && offset <= t.End && t.Identifier);
        if (index < 0)
            return result;
        var token = file.Tokens[index];
        var symbol = file.Declarations.TryGetValue(token.Start, out var declared) ? declared : Expression(file, index);
        if (symbol != null)
        {
            if (symbol.Offset >= 0)
                result.Definition = new AnalysisDefinition { File = symbol.File, Offset = symbol.Offset };
            result.SymbolKind = symbol.ColorKind.Length > 0 ? symbol.ColorKind : symbol.Kind;
            result.HoverText = symbol.Signature + (symbol.Documentation.Length == 0 ? "" : "\n\n" + symbol.Documentation);
        }
        else
            result.IsGameBuiltin = ScalaSyntaxHighlighter.IsBuiltinName(token.Text) && (index == 0 || file.Tokens[index - 1].Text != ".");
        return result;
    }
    public IReadOnlyList<SemanticSpan> Spans(string fileName)
    {
        if (!files.TryGetValue(fileName, out var file))
            return Array.Empty<SemanticSpan>();
        var spans = new List<SemanticSpan>();
        for (int i = 0; i < file.Tokens.Count; i++)
        {
            var token = file.Tokens[i];
            if (!token.Identifier)
                continue;
            var symbol = file.Declarations.TryGetValue(token.Start, out var declared) ? declared : Expression(file, i);
            if (symbol != null)
                spans.Add(new SemanticSpan { Start = token.Start, Length = token.Length, Kind = symbol.ColorKind.Length > 0 ? symbol.ColorKind : file.Declarations.ContainsKey(token.Start) && symbol.Kind == "function" ? "functionDeclaration" : symbol.Kind });
        }
        return spans;
    }
    public bool TryMembers(string fileName, int cursor, out List<string> members)
    {
        members = new List<string>();
        if (!files.TryGetValue(fileName, out var file))
            return false;
        int at = file.Tokens.FindLastIndex(t => t.End <= cursor && t.Kind != ScalaTokenKind.Newline);
        if (at >= 0 && file.Tokens[at].Identifier && file.Tokens[at].Text != ".")
            at--;
        if (at < 1 || file.Tokens[at].Text != ".")
            return false;
        var receiver = Expression(file, at - 1);
        var type = receiver == null ? null : ValueType(file, receiver, 0);
        if (type == null)
            return false;
        members = Members(files[type.File], type, new HashSet<Symbol>()).Select(s => s.Name).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        return true;
    }
    public List<string> Complete(string fileName, int cursor)
    {
        if (TryMembers(fileName, cursor, out var members))
            return members;
        if (!files.TryGetValue(fileName, out var file))
            return new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var scope = At(file, cursor); scope != null; scope = scope.Parent)
            foreach (var symbol in scope.Names)
                names.Add(symbol.Name);
        foreach (string name in file.Imports.Keys)
            // Unresolved SDK/library imports need compiler identity before they
            // can be offered, or a locked game alias could leak through fallback.
            if (Lookup(file, At(file, cursor), name) != null)
                names.Add(name);
        foreach (string module in file.Wildcards)
            foreach (var symbol in Exports(module))
                names.Add(symbol.Name);
        foreach (var symbol in Exports(""))
            names.Add(symbol.Name);
        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}

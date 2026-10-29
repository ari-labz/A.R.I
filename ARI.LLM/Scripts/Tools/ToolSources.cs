using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>The built-in tools' source, shipped inside the assembly as text (see ARI.LLM.csproj) so the Tools tab
/// can show how any tool works. A tool is matched to its file by the name it declares, so there is no list to keep up to date.</summary>
internal static class ToolSources
{
    private static Dictionary<string, (string File, string Code)>? index;
    private static readonly object Gate = new();

    private static readonly Regex DeclaredName = new(@"\bName\s*=>\s*""([a-z][a-z0-9_]*)""|\bname\s*=\s*""([a-z][a-z0-9_]*)""|^\s*\[Tool\(\s*""([a-z][a-z0-9_]*)""", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex FactoryKey   = new(@"\[""([a-z][a-z0-9_]*)""\]\s*=", RegexOptions.Compiled);

    /// <summary>The file a built-in tool is defined in, and its text. Null when it can't be found.</summary>
    internal static (string File, string Code)? For(string toolName)
    {
        lock (Gate) index ??= Build();
        return index.TryGetValue(toolName, out var hit) ? hit : null;
    }

    // One C# string literal, or several joined with +, as written after "description =".
    private static readonly Regex Literal = new(@"description\s*=\s*((?:\$?@?""(?:\\.|""""|[^""\\])*""\s*\+?\s*)+)", RegexOptions.Compiled);
    private static readonly Regex Piece = new(@"""((?:\\.|[^""\\])*)""", RegexOptions.Compiled);

    /// <summary>The description a built-in declares in its schema, read from its source. For tools that can't be built
    /// without a running module or a bound project, so their description can still be shown.</summary>
    internal static string DescriptionOf(string toolName)
    {
        if (For(toolName) is not { } src) return "";
        Match decl = Regex.Match(src.Code, $@"\bname\s*=\s*""{Regex.Escape(toolName)}""");
        if (!decl.Success) return "";
        Match m = Literal.Match(src.Code, decl.Index);
        if (!m.Success) return "";
        string text = string.Concat(Piece.Matches(m.Groups[1].Value).Select(p => Regex.Unescape(p.Groups[1].Value)));
        return text.Length > 300 ? text[..300].TrimEnd() + "…" : text;
    }

    private static Dictionary<string, (string File, string Code)> Build()
    {
        Dictionary<string, (string File, string Code)> map = new(StringComparer.OrdinalIgnoreCase);
        System.Reflection.Assembly asm = typeof(ToolSources).Assembly;
        List<(string File, string Code)> files = new();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("ToolSource/", StringComparison.Ordinal)))
        {
            using Stream? s = asm.GetManifestResourceStream(res);
            if (s is null) continue;
            using StreamReader r = new(s);
            files.Add((res["ToolSource/".Length..].Replace('\\', '/'), r.ReadToEnd()));
        }

        // A tool's own file first; ToolFactories.cs only for tools that are defined inline in it.
        foreach ((string file, string code) in files.Where(f => !f.File.EndsWith("ToolFactories.cs") && !f.File.StartsWith("Custom/")))
            foreach (Match m in DeclaredName.Matches(code))
            {
                string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                map.TryAdd(name, (file, code));
            }
        foreach ((string file, string code) in files.Where(f => f.File.EndsWith("ToolFactories.cs")))
            foreach (Match m in FactoryKey.Matches(code))
                map.TryAdd(m.Groups[1].Value, (file, code));
        return map;
    }
}

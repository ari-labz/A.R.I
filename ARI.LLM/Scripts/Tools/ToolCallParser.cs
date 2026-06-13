using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// Stateless parsing for the non-native tool-call formats local models emit, plus
/// repair and trimming of tool-call argument JSON. The streaming/execution loop that
/// drives these lives in <see cref="Thread"/>; only the format handling lives here.
/// </summary>
internal static class ToolCallParser
{
    internal record Call(string Id, string Name, string Args);
    internal record XmlParse(List<Call> Calls, int FirstIndex);

    /// <summary>Parses the text tool-call format: &lt;tool_call&gt;&lt;function=name&gt;...&lt;/function&gt;&lt;/tool_call&gt;. Null if none.</summary>
    internal static List<Call>? ParseTextCalls(string text)
    {
        MatchCollection matches = Regex.Matches(
            text,
            @"<tool_call>\s*<function=(\w+)>(.*?)</function>\s*</tool_call>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;

        List<Call> calls = new();
        int index = 0;
        foreach (Match m in matches)
        {
            string        name        = m.Groups[1].Value.Trim();
            string        callBody    = m.Groups[2].Value;
            StringBuilder argsBuilder = new();
            argsBuilder.Append('{');
            bool first = true;
            foreach (Match p in Regex.Matches(callBody, @"<parameter=(\w+)>\s*(.*?)\s*</parameter>", RegexOptions.Singleline))
            {
                if (!first) argsBuilder.Append(',');
                argsBuilder.Append($"\"{p.Groups[1].Value}\":\"{p.Groups[2].Value.Trim()}\"");
                first = false;
            }
            argsBuilder.Append('}');
            calls.Add(new Call($"fallback_{++index}", name, argsBuilder.ToString()));
        }
        return calls;
    }

    /// <summary>Parses the Qwen3 XML tool-call format: &lt;tool_name&gt;&lt;param&gt;value&lt;/param&gt;...&lt;/tool_name&gt;. Null if none.</summary>
    internal static XmlParse? ParseXmlCalls(string text, IEnumerable<string> toolNames)
    {
        string toolNamePattern = string.Join("|", toolNames.Select(Regex.Escape));
        if (toolNamePattern.Length == 0) return null;

        MatchCollection matches = Regex.Matches(
            text,
            $@"<({toolNamePattern})>\s*(.*?)\s*</\1>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;

        List<Call> calls = new();
        int index = 0;
        foreach (Match m in matches)
        {
            string toolName = m.Groups[1].Value.Trim().ToLowerInvariant();
            string inner    = m.Groups[2].Value;

            Dictionary<string, string> argsDict = new(StringComparer.OrdinalIgnoreCase);
            foreach (Match p in Regex.Matches(inner, @"<(\w+)>\s*(.*?)\s*</\1>", RegexOptions.Singleline))
            {
                string paramName = p.Groups[1].Value.Trim();
                if (paramName.Equals("file_path", StringComparison.OrdinalIgnoreCase)) paramName = "path";
                argsDict[paramName] = p.Groups[2].Value.Trim();
            }

            calls.Add(new Call($"fallback_xml_{++index}", toolName, JsonSerializer.Serialize(argsDict)));
        }
        return new XmlParse(calls, matches[0].Index);
    }

    /// <summary>
    /// Strips &lt;think&gt;...&lt;/think&gt; blocks that leak into tool call argument streams from Qwen3.
    /// llama.cpp forces thinking internally even when enable_thinking is false, and the closing
    /// &lt;/think&gt; tag can arrive mid-argument delta, corrupting the JSON.
    /// Three cases handled:
    ///   1. Complete &lt;think&gt;...&lt;/think&gt; block inside args — stripped entirely.
    ///   2. Orphaned &lt;/think&gt; (think opened before tool call, closed mid-arg) — truncate at tag.
    ///   3. Orphaned &lt;think&gt; (think opened mid-arg, never closed) — truncate at tag.
    /// </summary>
    internal static string StripThinkLeaks(string argsJson)
    {
        // Case 1: complete blocks
        argsJson = Regex.Replace(argsJson, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Case 2: orphaned close tag — everything from </think> onward is garbage (model exited think mode mid-arg)
        int closeIdx = argsJson.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (closeIdx >= 0)
            argsJson = argsJson[..closeIdx];

        // Case 3: orphaned open tag — model entered think mode mid-arg
        int openIdx = argsJson.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (openIdx >= 0)
            argsJson = argsJson[..openIdx];

        return argsJson.TrimEnd();
    }

    /// <summary>
    /// Escapes any bare double-quotes that appear inside JSON string values. Quantized local
    /// models occasionally emit {"key": "val"ue"} instead of {"key": "val\"ue"}.
    /// </summary>
    internal static string RepairArgs(string argsJson)
    {
        StringBuilder sb     = new(argsJson.Length);
        bool          inStr  = false;
        bool          escape = false;

        for (int i = 0; i < argsJson.Length; i++)
        {
            char c = argsJson[i];

            if (escape)
            {
                sb.Append(c);
                escape = false;
                continue;
            }

            if (c == '\\')
            {
                sb.Append(c);
                escape = true;
                continue;
            }

            if (c == '"')
            {
                if (!inStr)
                {
                    inStr = true;
                    sb.Append(c);
                    continue;
                }

                // Peek ahead: if the next non-whitespace char is : , } ] then this quote
                // legitimately closes the string; otherwise it's an unescaped inner quote.
                int j = i + 1;
                while (j < argsJson.Length && argsJson[j] == ' ') j++;
                char next = j < argsJson.Length ? argsJson[j] : '\0';

                if (next is ':' or ',' or '}' or ']' or '\0')
                {
                    inStr = false;
                    sb.Append(c);
                }
                else
                {
                    sb.Append("\\\"");
                }
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips large content fields from write_file / edit_file args before they go into the
    /// messages array, so they don't bloat the context window on subsequent LLM turns.
    /// </summary>
    internal static string TrimArgs(string toolName, string argsJson)
    {
        if (toolName is not ("write_file" or "edit_file")) return argsJson;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;
            Dictionary<string, object?> trimmed = new();
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (toolName == "write_file" && prop.Name == "content")
                    trimmed[prop.Name] = "[content omitted]";
                else if (toolName == "edit_file" && prop.Name is "old_string" or "new_string")
                    trimmed[prop.Name] = "[omitted]";
                else
                    trimmed[prop.Name] = prop.Value.GetRawText();
            }
            return JsonSerializer.Serialize(trimmed);
        }
        catch { return argsJson; }
    }

    internal static bool IsError(string result) =>
        result.StartsWith("[Error:", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

    internal static string EscapeLabel(string s) =>
        s.Replace("--", "&#45;&#45;").Replace(">", "&gt;");
}

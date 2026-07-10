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
            foreach (Match p in Regex.Matches(callBody, @"<parameter=(\w+)>(.*?)</parameter>", RegexOptions.Singleline))
            {
                if (!first) argsBuilder.Append(',');
                string pName = p.Groups[1].Value;
                string pVal  = TrimParamValue(pName, p.Groups[2].Value);
                argsBuilder.Append(JsonSerializer.Serialize(pName));
                argsBuilder.Append(':');
                // Structured params (e.g. edit_file's `edits`) are emitted by the model as
                // a JSON array. Embed them as raw JSON so the executor receives an array, not a stringified
                // one (which the executor would reject as missing start_line/end_line). All other
                // values (code in new_string/content, paths, patterns) are serialized as JSON strings so
                // their quotes/newlines/backslashes can't break the args JSON.
                if (IsStructuredParam(pName) && IsJsonArrayOrObject(pVal))
                    argsBuilder.Append(pVal);
                else
                    argsBuilder.Append(JsonSerializer.Serialize(pVal));
                first = false;
            }
            argsBuilder.Append('}');
            calls.Add(new Call($"fallback_{++index}", name, UnwrapArgumentsParam(argsBuilder.ToString())));
        }
        return calls;
    }

    /// <summary>
    /// Models occasionally wrap the whole argument object in a single &lt;parameter=arguments&gt; block
    /// (e.g. &lt;parameter=arguments&gt;{"path": "...", "start_line": 25}&lt;/parameter&gt;), which parses to
    /// {"arguments":"{...}"} — the executor then sees no real fields and dispatches with an empty path.
    /// Unwrap it: if the ONLY parameter is 'arguments' and its value is itself a JSON object, use that.
    /// </summary>
    private static string UnwrapArgumentsParam(string argsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return argsJson;
            JsonProperty[] props = root.EnumerateObject().ToArray();
            if (props.Length != 1 || !props[0].Name.Equals("arguments", StringComparison.OrdinalIgnoreCase)) return argsJson;

            string inner = props[0].Value.ValueKind == JsonValueKind.String
                ? props[0].Value.GetString() ?? ""
                : props[0].Value.GetRawText();
            inner = inner.Trim();
            if (inner.StartsWith('{') && IsJsonArrayOrObject(inner)) return inner;
            return argsJson;
        }
        catch { return argsJson; }
    }

    /// <summary>Trims a text-format parameter value. Code-bearing params (new_string/content/old_string)
    /// keep their indentation: only the newline the model puts after the open tag and the newline (plus
    /// any tag-indent spaces) before the close tag are stripped — a full Trim() would delete the first
    /// code line's leading whitespace and every edit would land flush-left (the Coder then sees the
    /// misindented echo and loops re-fixing it). All other params (paths, patterns, line numbers) get
    /// the full trim as before.</summary>
    private static string TrimParamValue(string name, string raw)
    {
        if (!IsContentParam(name)) return raw.Trim();
        string v = Regex.Replace(raw, @"^[ \t]*\r?\n", "");
        return Regex.Replace(v, @"\r?\n[ \t]*$", "");
    }

    /// <summary>Tool parameters that carry code/file content, where leading whitespace is significant.</summary>
    private static bool IsContentParam(string name) =>
        name.Equals("new_string", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("content",    StringComparison.OrdinalIgnoreCase) ||
        name.Equals("old_string", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tool parameters whose value the model emits as a JSON array/object (not a string).</summary>
    private static bool IsStructuredParam(string name) =>
        name.Equals("edits", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("todos", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the trimmed value is parseable JSON that starts as an array or object.</summary>
    private static bool IsJsonArrayOrObject(string s)
    {
        if (s.Length < 2 || (s[0] != '[' && s[0] != '{')) return false;
        try { using JsonDocument _ = JsonDocument.Parse(s); return true; }
        catch { return false; }
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
            foreach (Match p in Regex.Matches(inner, @"<(\w+)>(.*?)</\1>", RegexOptions.Singleline))
            {
                string paramName = p.Groups[1].Value.Trim();
                if (paramName.Equals("file_path", StringComparison.OrdinalIgnoreCase)) paramName = "path";
                argsDict[paramName] = TrimParamValue(paramName, p.Groups[2].Value);
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
    /// Salvages a native tool call's arguments when the model leaked text-format markers
    /// (&lt;function=…&gt;, &lt;parameter=…&gt;, &lt;tool_call&gt;) into what should be pure JSON — a
    /// repetition/format-mix runaway. Truncates at the first marker and balances any dangling
    /// string/object so the prefix parses (e.g. {"pattern":"foo → {"pattern":"foo"}).
    /// </summary>
    internal static string SalvageNativeArgs(string raw)
    {
        // Cut at the first leaked text-format marker, then rebuild clean JSON from the field values
        // in the prefix — stripping leaked whitespace/control chars out of each value and properly
        // escaping it. This recovers the first call's real arguments (e.g. a read_file path) from a
        // runaway native-arg blob, producing JSON that always parses.
        int cut = raw.Length;
        foreach (string m in new[] { "<tool_call", "</tool_call", "<function", "</function", "<parameter", "</parameter" })
        {
            int i = raw.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < cut) cut = i;
        }
        string s = raw[..cut];

        List<string> fields = new();
        // Capture each "key":"value" preserving the model's existing JSON escaping — the value body
        // matches proper JSON string syntax ((?:\\.|[^"\\])*: an escape sequence, or any non-quote/
        // non-backslash char), so \". and \\ inside the value are kept intact and the match stops at
        // the first *unescaped* closing quote. We emit the value VERBATIM (no re-escaping — re-escaping
        // already-valid content double-escapes regex backslashes, e.g. "\\.IsAdmin" → "\\\\.IsAdmin").
        // We only strip real control chars (invalid in JSON) and trailing truncation noise (a dangling
        // \n/\r/\t or whitespace left where the runaway tail was cut).
        foreach (Match m in Regex.Matches(s, "\"(\\w+)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)", RegexOptions.Singleline))
        {
            string val = m.Groups[2].Value
                .Replace("\r", "").Replace("\n", " ").Replace("\t", " ");
            val = Regex.Replace(val, @"(?:\\[nrt]|\s)+$", "");
            fields.Add($"\"{m.Groups[1].Value}\":\"{val}\"");
        }
        foreach (Match m in Regex.Matches(s, "\"(\\w+)\"\\s*:\\s*(-?\\d+)"))
            fields.Add($"\"{m.Groups[1].Value}\":{m.Groups[2].Value}");

        return fields.Count == 0 ? "{}" : "{" + string.Join(",", fields) + "}";
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
            // Build the object by hand. Kept fields are emitted as their RAW JSON text — they are
            // already valid JSON, so re-serializing them (e.g. via a Dictionary<string,string> of
            // GetRawText()) double-encodes: path:"foo" → path:"\"foo\"", start_line:81 → "81". That
            // double-encoding was the escape-spiral root cause (the model imitates the quoting each
            // turn and it compounds). Only the omitted payload fields are emitted as fresh strings.
            StringBuilder sb = new();
            sb.Append('{');
            bool first = true;
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(prop.Name));
                sb.Append(':');
                // Keep BOTH edit_file AND write_file payloads in full — the model must be able to see its
                // OWN edits. Omitting write_file content (it can be a whole file) caused a copy-forward doom
                // loop: the model couldn't see what it wrote, assumed it wrote the wrong thing, re-read the
                // file (finding it correct), then re-sent the "[content omitted]" placeholder from history as
                // the new content — forever. With context now kept lean elsewhere (previews, phase pruning),
                // we can afford to keep the real content so there is no placeholder to copy.
                sb.Append(prop.Value.GetRawText());
            }
            sb.Append('}');
            return sb.ToString();
        }
        catch { return argsJson; }
    }

    internal static bool IsError(string result) =>
        result.StartsWith("[Error:", StringComparison.OrdinalIgnoreCase) ||
        result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

    internal static string EscapeLabel(string s) =>
        s.Replace("--", "&#45;&#45;").Replace(">", "&gt;");

    /// <summary>
    /// Extracts a complete top-level string property value from a possibly-incomplete JSON
    /// object (a tool call's arguments while they are still streaming). Returns the unescaped
    /// value once its closing quote has arrived, or null if the property is absent or its value
    /// is not yet complete. Used for streaming fail-fast so a precondition (e.g. read-before-edit)
    /// can be checked the moment the relevant field lands, without waiting for the whole call.
    /// </summary>
    internal static string? TryExtractJsonString(string partialJson, string key)
    {
        string needle = "\"" + key + "\"";
        int k = partialJson.IndexOf(needle, StringComparison.Ordinal);
        if (k < 0) return null;

        int i = k + needle.Length;
        // Skip whitespace and the ':' separator.
        while (i < partialJson.Length && (char.IsWhiteSpace(partialJson[i]) || partialJson[i] == ':')) i++;
        if (i >= partialJson.Length || partialJson[i] != '"') return null; // value not a string, or not started
        i++; // past the opening quote

        System.Text.StringBuilder sb = new();
        while (i < partialJson.Length)
        {
            char c = partialJson[i];
            if (c == '\\')
            {
                if (i + 1 >= partialJson.Length) return null; // escape sequence not yet complete
                char n = partialJson[i + 1];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\\' => '\\', '/' => '/', _ => n });
                i += 2;
                continue;
            }
            if (c == '"') return sb.ToString(); // closing quote reached — value complete
            sb.Append(c);
            i++;
        }
        return null; // closing quote not yet streamed
    }
}

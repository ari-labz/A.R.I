namespace ARI.LLM;

/// <summary>
/// Pulls the COMPLETE top-level objects out of a JSON array that may have been cut off mid-way — a model that hit
/// its token cap leaves <c>[{...},{...},{"entity":"x","exc</c>, which no JSON parser accepts, so the whole answer
/// used to be thrown away. String- and escape-aware, so braces inside excerpts don't confuse it.
/// </summary>
internal static class JsonObjectSalvage
{
    /// <summary>The text of each fully closed <c>{...}</c> object directly inside the first array in
    /// <paramref name="content"/>. An unfinished trailing object is dropped.</summary>
    internal static List<string> CompleteObjects(string content)
    {
        List<string> objects = new();
        int arrayStart = content.IndexOf('[');
        if (arrayStart < 0) return objects;

        int  depth     = 0;   // brace depth inside the array
        int  objStart  = -1;
        bool inString  = false;
        bool escaped   = false;
        for (int i = arrayStart + 1; i < content.Length; i++)
        {
            char c = content[i];
            if (inString)
            {
                if (escaped)        escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"')  inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{':
                    if (depth == 0) objStart = i;
                    depth++;
                    break;
                case '}':
                    if (depth > 0 && --depth == 0 && objStart >= 0)
                    {
                        objects.Add(content[objStart..(i + 1)]);
                        objStart = -1;
                    }
                    break;
                case ']':
                    if (depth == 0) return objects;   // the array closed properly
                    break;
            }
        }
        return objects;
    }
}

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>Turns a tool class's [Param] members into the schema Ari reads, and her text arguments back into those
/// members. Ari only ever types text, so the schema says "string" for everything and the conversion to int, bool,
/// enums and lists happens here, with an error she can act on when it doesn't fit.</summary>
internal static class ToolParams
{
    internal sealed record Param(string Name, MemberInfo Member, Type Type, string Description, bool Optional, string[]? Options);

    internal static List<Param> Describe(Type type)
    {
        List<Param> list = new();
        foreach (MemberInfo m in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.GetCustomAttribute<ParamAttribute>() is not { } attr) continue;
            Type t = m switch { FieldInfo f => f.FieldType, PropertyInfo { CanWrite: true } p => p.PropertyType, _ => null! };
            if (t is null) continue;
            Type inner = Nullable.GetUnderlyingType(t) ?? t;
            string[]? options = attr.Options?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                ?? (inner.IsEnum ? Enum.GetNames(inner) : null);
            list.Add(new Param(Snake(m.Name), m, t, attr.Description, attr.Optional || Nullable.GetUnderlyingType(t) is not null, options));
        }
        return list;
    }

    internal static object Schema(string name, string description, List<Param> ps)
    {
        Dictionary<string, object> props = new();
        foreach (Param p in ps)
        {
            string note = IsList(p.Type) ? " Give several as a comma-separated list." : "";
            props[p.Name] = p.Options is { Length: > 0 }
                ? new { type = "string", description = p.Description + note, @enum = p.Options }
                : new { type = "string", description = p.Description + note };
        }
        return new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters = new { type = "object", properties = props, required = ps.Where(p => !p.Optional).Select(p => p.Name).ToArray() }
            }
        };
    }

    internal static void Bind(Tool target, string argsJson)
    {
        JsonElement root = default;
        bool hasObject = false;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try { root = JsonDocument.Parse(argsJson).RootElement; hasObject = root.ValueKind == JsonValueKind.Object; }
            catch (JsonException) { throw new ToolArgException("the arguments weren't valid JSON."); }
        }

        foreach (Param p in Describe(target.GetType()))
        {
            string? text = null;
            if (hasObject && root.TryGetProperty(p.Name, out JsonElement el))
                text = el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    JsonValueKind.Array  => string.Join(",", el.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                    _ => el.GetRawText(),
                };

            if (string.IsNullOrWhiteSpace(text))
            {
                if (!p.Optional) throw new ToolArgException($"missing required parameter '{p.Name}' ({p.Description}).");
                continue;   // leave whatever default the field has
            }
            if (p.Options is { Length: > 0 } opts && !IsList(p.Type) && !opts.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase))
                throw new ToolArgException($"'{p.Name}' must be one of: {string.Join(", ", opts)}.");

            object? value = Convert(p, text);
            if (p.Member is FieldInfo f) f.SetValue(target, value); else ((PropertyInfo)p.Member).SetValue(target, value);
        }
    }

    private static bool IsList(Type t) => t != typeof(string) && (t.IsArray || (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t)));

    private static object? Convert(Param p, string text)
    {
        Type t = p.Type;
        if (IsList(t))
        {
            Type elem = t.IsArray ? t.GetElementType()! : t.GetGenericArguments()[0];
            string[] parts = text.Split(new[] { ',', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Array arr = Array.CreateInstance(elem, parts.Length);
            for (int i = 0; i < parts.Length; i++) arr.SetValue(One(p.Name, parts[i], elem), i);
            if (t.IsArray) return arr;
            object list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elem), arr)!;
            return t.IsAssignableFrom(list.GetType()) ? list : throw new ToolArgException($"'{p.Name}' can't be filled from text.");
        }
        return One(p.Name, text, t);
    }

    private static object? One(string name, string raw, Type type)
    {
        string s = raw.Trim();
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            if (t == typeof(string))  return raw;
            if (t == typeof(bool))    return s.ToLowerInvariant() switch { "true" or "yes" or "y" or "1" or "on" => true, "false" or "no" or "n" or "0" or "off" => false, _ => throw new FormatException() };
            if (t.IsEnum)             return Enum.Parse(t, s, ignoreCase: true);
            if (t == typeof(Guid))    return Guid.Parse(s);
            if (t == typeof(Uri))     return new Uri(s, UriKind.RelativeOrAbsolute);
            if (t == typeof(DateTime))       return DateTime.Parse(s, CultureInfo.InvariantCulture);
            if (t == typeof(DateTimeOffset)) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
            if (t == typeof(TimeSpan))       return TimeSpan.Parse(s, CultureInfo.InvariantCulture);
            return System.Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            throw new ToolArgException($"'{name}' should be {Friendly(t)}, but got \"{raw}\".");
        }
    }

    private static string Friendly(Type t) =>
        t == typeof(bool) ? "true or false" :
        t.IsEnum ? "one of: " + string.Join(", ", Enum.GetNames(t)) :
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? "a whole number" :
        t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? "a number" :
        t == typeof(DateTime) || t == typeof(DateTimeOffset) ? "a date and time" :
        t == typeof(TimeSpan) ? "a duration like 01:30:00" : "a " + t.Name.ToLowerInvariant();

    internal static string Snake(string name) => Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();
}

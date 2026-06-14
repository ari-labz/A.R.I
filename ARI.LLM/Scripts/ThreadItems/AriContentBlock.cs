using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// One ordered piece of an <see cref="AriResponse"/> — either prose or a tool-use card.
/// A block's <see cref="ToString"/> is its exact rendered text, so joining the blocks of a
/// response reproduces the original byte-for-byte (the frontend renders that joined string).
/// </summary>
public abstract class AriContentBlock
{
    public abstract override string ToString();

    // Recognises the tool markers the streaming loop emits. Everything else is prose.
    private static readonly Regex MarkerRe = new(
        @"(?<div><div class=""tool-use"">(?<dinner>.*?)</div>)" +
        @"|(?<start><!--ari-tool-start:(?<sname>[^:]+):(?<slabel>[^>]*?)-->)" +
        @"|(?<end><!--ari-tool-end:(?<ename>[^:]+):(?<elabel>[^>]*?)-->)" +
        @"|(?<err><!--ari-tool-error:(?<rname>[^:]+):(?<rlabel>[^>]*?)-->)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Splits a rendered response string into ordered text and card blocks, preserving every byte.</summary>
    public static List<AriContentBlock> Parse(string content)
    {
        List<AriContentBlock> blocks = new();
        int pos = 0;

        foreach (Match m in MarkerRe.Matches(content))
        {
            if (m.Index > pos)
                blocks.Add(new TextBlock { Text = content[pos..m.Index] });

            blocks.Add((AriContentBlock?)ToCard(m) ?? new TextBlock { Text = m.Value });
            pos = m.Index + m.Length;
        }

        if (pos < content.Length)
            blocks.Add(new TextBlock { Text = content[pos..] });

        return blocks;
    }

    private static Card? ToCard(Match m)
    {
        if (m.Groups["div"].Success)
        {
            string inner = m.Groups["dinner"].Value;
            int    space = inner.IndexOf(' ');
            string verb  = space < 0 ? inner : inner[..space];
            string label = space < 0 ? ""    : inner[(space + 1)..];
            return Build(FromVerb(verb), CardState.Active, m.Value, label);
        }
        if (m.Groups["start"].Success)
            return Build(FromTool(m.Groups["sname"].Value), CardState.Active, m.Value, m.Groups["slabel"].Value);
        if (m.Groups["end"].Success)
            return Build(FromTool(m.Groups["ename"].Value), CardState.Done, m.Value, m.Groups["elabel"].Value);
        if (m.Groups["err"].Success)
            return Build(FromTool(m.Groups["rname"].Value), CardState.Error, m.Value, m.Groups["rlabel"].Value);
        return null;
    }

    private static Card? Build(Card? card, CardState state, string marker, string label)
    {
        if (card is null) return null;
        card.State  = state;
        card.Marker = marker;
        card.Fill(label);
        return card;
    }

    private static Card? FromTool(string name) => name switch
    {
        "read_file"      => new Reading(),
        "list_directory" => new Listing(),
        "search_files"   => new Searching(),
        "edit_file"      => new Editing(),
        "write_file"     => new Writing(),
        "run_command"    => new Running(),
        "find_files"     => new Finding(),
        "delete_file"    => new Deleting(),
        "move_file"      => new Moving(),
        "update_todos"   => new TodoList(),
        _                => null
    };

    private static Card? FromVerb(string verb) => verb switch
    {
        "Reading"   => new Reading(),
        "Listing"   => new Listing(),
        "Searching" => new Searching(),
        "Editing"   => new Editing(),
        "Writing"   => new Writing(),
        "Running"   => new Running(),
        "Finding"   => new Finding(),
        "Deleting"  => new Deleting(),
        "Moving"    => new Moving(),
        _           => null
    };
}

/// <summary>A run of prose between (or around) tool cards.</summary>
public sealed class TextBlock : AriContentBlock
{
    public string Text { get; set; } = "";
    public override string ToString() => Text;
}

public enum CardState { Active, Done, Error }

/// <summary>A tool-use card. Renders as the exact marker the tool emitted.</summary>
public abstract class Card : AriContentBlock
{
    public CardState State  { get; set; }
    public string    Marker { get; set; } = "";

    public override string ToString() => Marker;

    /// <summary>Populates this card's typed fields from the marker's label.</summary>
    protected internal abstract void Fill(string label);
}

public sealed class Reading : Card
{
    public string File { get; set; } = "";
    protected internal override void Fill(string label) => File = label;
}

public sealed class Listing : Card
{
    public string Path { get; set; } = "";
    protected internal override void Fill(string label) => Path = label;
}

public sealed class Searching : Card
{
    public string Pattern { get; set; } = "";
    protected internal override void Fill(string label) => Pattern = label;
}

public sealed class Running : Card
{
    public string Command { get; set; } = "";
    protected internal override void Fill(string label) => Command = label;
}

public sealed class Finding : Card
{
    public string Pattern { get; set; } = "";
    protected internal override void Fill(string label) => Pattern = label;
}

public sealed class Deleting : Card
{
    public string File { get; set; } = "";
    protected internal override void Fill(string label) => File = label;
}

public sealed class Moving : Card
{
    public string File { get; set; } = "";
    protected internal override void Fill(string label) => File = label;
}

/// <summary>The task checklist card. The label carries the list base64-encoded; the frontend decodes
/// and renders it. Being a Card means it is stripped from <see cref="AriResponse.ContextText"/>.</summary>
public sealed class TodoList : Card
{
    public string Encoded { get; set; } = "";
    protected internal override void Fill(string label) => Encoded = label;
}

/// <summary>A card that carries diff stats and an optional patch (edit/write).</summary>
public abstract class DiffCard : Card
{
    public string  File    { get; set; } = "";
    public int     Added   { get; set; }
    public int     Removed { get; set; }
    public string? Patch   { get; set; }

    // Label form: "File|+12|-5|<base64 patch>" (counts and patch optional).
    protected internal override void Fill(string label)
    {
        string[] parts = label.Split('|');
        File = parts.Length > 0 ? parts[0] : "";
        foreach (string part in parts.Skip(1))
        {
            if      (part.StartsWith('+') && int.TryParse(part[1..], out int add)) Added   = add;
            else if (part.StartsWith('-') && int.TryParse(part[1..], out int del)) Removed = del;
            else if (part.Length > 0)                                             Patch   = part;
        }
    }
}

public sealed class Editing : DiffCard { }
public sealed class Writing : DiffCard { }

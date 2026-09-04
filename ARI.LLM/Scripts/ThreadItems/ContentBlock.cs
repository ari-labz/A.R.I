using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ARI.LLM;

/// <summary>
/// One ordered piece of a <see cref="Response"/> — prose, reasoning, or a tool-use card.
/// Each block carries its own lifecycle <see cref="State"/> and visibility:
/// <see cref="IsVisible"/> controls whether the user sees it, while <see cref="AdditionalContext"/>
/// holds text that is hidden from the user but still fed to the model (e.g. a card's tool result).
/// Serialised polymorphically (a <c>type</c> discriminator) so the client renders typed blocks directly.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(Thinking),  "thinking")]
[JsonDerivedType(typeof(Reading),    "reading")]
[JsonDerivedType(typeof(Previewing), "previewing")]
[JsonDerivedType(typeof(Reverting),  "reverting")]
[JsonDerivedType(typeof(Deleting),  "deleting")]
[JsonDerivedType(typeof(Moving),    "moving")]
[JsonDerivedType(typeof(Listing),   "listing")]
[JsonDerivedType(typeof(Searching), "searching")]
[JsonDerivedType(typeof(WebSearching), "webSearching")]
[JsonDerivedType(typeof(Browsing),     "browsing")]
[JsonDerivedType(typeof(Finding),   "finding")]
[JsonDerivedType(typeof(Running),    "running")]
[JsonDerivedType(typeof(Delegating), "delegating")]
[JsonDerivedType(typeof(Building),   "building")]
[JsonDerivedType(typeof(Editing),   "editing")]
[JsonDerivedType(typeof(Writing),   "writing")]
[JsonDerivedType(typeof(Subthread), "subthread")]
[JsonDerivedType(typeof(PlanProposed), "plan")]
[JsonDerivedType(typeof(PersonaEdit),  "personaEdit")]
[JsonDerivedType(typeof(ModeSwitch),   "mode")]
public abstract class ContentBlock
{
    // ── Constants ────────────────────────────────────────────────────────────────
    protected const int MAX_TITLE_DISPLAY_LENGTH = 90;

    /// <summary>Lifecycle of this block. Streaming until finished/flipped; Error on failure.</summary>
    public State State { get; set; } = State.Streaming;

    /// <summary>Whether the user sees this block in the chat UI.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Optional text hidden from the user but included in the model's context on later turns
    /// (e.g. a tool result behind a card). Null when the block contributes nothing extra to context.
    /// Server-only — never serialised to the client.</summary>
    [JsonIgnore]
    public string? AdditionalContext { get; set; }

    public abstract override string ToString();

    // ── Legacy marker parsing (removed in the streaming-into-blocks rework). ──
    // Recognises the tool markers the streaming loop emits. Everything else is prose.
    private static readonly Regex MarkerRe = new(
        @"(?<div><div class=""tool-use"">(?<dinner>.*?)</div>)" +
        @"|(?<start><!--ari-tool-start:(?<sname>[^:]+):(?<slabel>[^>]*?)-->)" +
        @"|(?<done><!--ari-tool-done:(?<dname>[^:]+):(?<dlabel>[^>]*?)-->)" +
        @"|(?<end><!--ari-tool-end:(?<ename>[^:]+):(?<elabel>[^>]*?)-->)" +
        @"|(?<err><!--ari-tool-error:(?<rname>[^:]+):(?<rlabel>[^>]*?)-->)" +
        @"|(?<sub><!--ari-subthread:(?<subkey>[^|>]+)\|(?<sublabel>[^>]*?)-->)" +
        @"|(?<plan><!--ari-plan-proposed-->)" +
        @"|(?<persona><!--ari-persona-edit:(?<pid>[^>]*?)-->)" +
        @"|(?<mode><!--ari-tool-mode:(?<mname>[^:]+):(?<mlabel>[^>]*?)-->)" +
        @"|(?<batch><!--ari-batch-end-->)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Splits a rendered response string into ordered text and card blocks. Prose runs become
    /// <see cref="TextBlock"/>s; tool markers become finalized <see cref="Card"/>s. A diff edit's start and
    /// end markers are paired into ONE card (Complete once its batch closes, Error on failure); the
    /// <c>&lt;!--ari-batch-end--&gt;</c> separator is absorbed so it never pollutes the surrounding prose.</summary>
    public static List<ContentBlock> Parse(string content)
    {
        List<ContentBlock> blocks = new();
        int pos = 0;

        // Pending diff cards (edit_file / write_file) keyed by "name:file", awaiting their end/batch markers.
        Dictionary<string, DiffCard> pendingDiff = new();

        void FlushText(int upto)
        {
            if (upto > pos)
            {
                string text = content[pos..upto];
                if (text.Length > 0) blocks.Add(new TextBlock { Text = text });
            }
        }

        foreach (Match m in MarkerRe.Matches(content))
        {
            // A diff END marker finalizes its already-pushed start card in place — no new block, and the
            // marker itself is dropped from the text so the "Done…" prose that follows is a clean TextBlock.
            if (m.Groups["end"].Success)
            {
                FlushText(m.Index);
                string name = m.Groups["ename"].Value;
                if (FromTool(name) is DiffCard && pendingDiff.TryGetValue($"{name}:{DiffFile(m.Groups["elabel"].Value)}", out DiffCard? open))
                    open.Fill(m.Groups["elabel"].Value);   // enrich with counts + patch from the richer end label
                pos = m.Index + m.Length;
                continue;
            }
            // The batch separator flips every open diff card to Complete and is absorbed (produces no block).
            if (m.Groups["batch"].Success)
            {
                FlushText(m.Index);
                foreach (DiffCard c in pendingDiff.Values) c.Flip();
                pendingDiff.Clear();
                pos = m.Index + m.Length;
                continue;
            }

            // A subthread anchor: "render child thread <key> here". The child's blocks are spliced in by the
            // owning Response at serialization time (it holds the key→Thread map) — never flattened into context.
            if (m.Groups["sub"].Success)
            {
                FlushText(m.Index);
                blocks.Add(new Subthread { ChildKey = m.Groups["subkey"].Value, Label = m.Groups["sublabel"].Value });
                pos = m.Index + m.Length;
                continue;
            }

            FlushText(m.Index);
            Card? card = ToCard(m);
            if (card is not null)
            {
                blocks.Add(card);
                // Remember a diff START so its END/batch can finalize this same card instance.
                if (card is DiffCard d && m.Groups["start"].Success)
                    pendingDiff[$"{m.Groups["sname"].Value}:{DiffFile(m.Groups["slabel"].Value)}"] = d;
            }
            else
            {
                blocks.Add(new TextBlock { Text = m.Value });
            }
            pos = m.Index + m.Length;
        }

        FlushText(content.Length);
        return blocks;
    }

    private static string DiffFile(string label) { int bar = label.IndexOf('|'); return bar < 0 ? label : label[..bar]; }

    private static Card? ToCard(Match m)
    {
        if (m.Groups["div"].Success)
        {
            string inner = m.Groups["dinner"].Value;
            int    space = inner.IndexOf(' ');
            string verb  = (space < 0 ? inner : inner[..space]).TrimEnd(':');   // "Delegating:" → "Delegating"
            string label = space < 0 ? ""    : inner[(space + 1)..];
            return Build(FromVerb(verb), State.Streaming, m.Value, label);
        }
        if (m.Groups["start"].Success)
            return Build(FromTool(m.Groups["sname"].Value), State.Streaming, m.Value, m.Groups["slabel"].Value);
        if (m.Groups["done"].Success)
            return Build(FromTool(m.Groups["dname"].Value), State.Complete, m.Value, m.Groups["dlabel"].Value);
        if (m.Groups["end"].Success)
            return Build(FromTool(m.Groups["ename"].Value), State.Complete, m.Value, m.Groups["elabel"].Value);
        if (m.Groups["err"].Success)
            return Build(FromTool(m.Groups["rname"].Value), State.Error, m.Value, m.Groups["rlabel"].Value);
        if (m.Groups["plan"].Success)
            return Build(new PlanProposed(), State.Complete, m.Value, "");
        if (m.Groups["persona"].Success)
            return Build(new PersonaEdit(), State.Complete, m.Value, m.Groups["pid"].Value);
        if (m.Groups["mode"].Success)
            return Build(new ModeSwitch(), State.Complete, m.Value, m.Groups["mlabel"].Value);
        return null;
    }

    private static Card? Build(Card? card, State state, string marker, string label)
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
        "preview_file"   => new Previewing(),
        "list_directory" => new Listing(),
        "search_files"   => new Searching(),
        "search_web"     => new WebSearching(),
        "fetch_page"     => new Browsing(),
        "edit_file"      => new Editing(),
        "write_file"     => new Writing(),
        "run_command"    => new Running(),
        "find_files"     => new Finding(),
        "delete_file"    => new Deleting(),
        "move_file"      => new Moving(),
        "revert_file"    => new Reverting(),
        "spawn_coder"    => new Delegating(),
        "build_project"  => new Building(),
        _                => null
    };

    private static Card? FromVerb(string verb) => verb switch
    {
        "Reading"    => new Reading(),
        "Previewing" => new Previewing(),
        "Listing"    => new Listing(),
        "Searching"  => new Searching(),
        "Searching the web" => new WebSearching(),
        "Browsing"   => new Browsing(),
        "Editing"    => new Editing(),
        "Writing"    => new Writing(),
        "Running"    => new Running(),
        "Finding"    => new Finding(),
        "Deleting"   => new Deleting(),
        "Moving"     => new Moving(),
        "Reverting"  => new Reverting(),
        "Delegating" => new Delegating(),
        "Building"   => new Building(),
        _            => null
    };
}

/// <summary>A run of prose between (or around) tool cards — visible and re-entered into model context.</summary>
public sealed class TextBlock : ContentBlock
{
    public string Text { get; set; } = "";
    public override string ToString() => Text;
}

/// <summary>The model's reasoning / chain-of-thought, streamed as a block. Shown only when
/// <see cref="ContentBlock.IsVisible"/> is set; never re-enters context unless captured via
/// <see cref="ContentBlock.AdditionalContext"/>.</summary>
public sealed class Thinking : ContentBlock
{
    public string Text { get; set; } = "";
    public override string ToString() => Text;
}

/// <summary>A tool-use card. Renders as an <c>&lt;!--ari-tool-start:name:label--&gt;</c> marker while streaming
/// and a self-contained <c>&lt;!--ari-tool-done:name:label--&gt;</c> marker once <see cref="Flip"/>ped — ONE
/// marker = one card = one state, so every client renders every tool identically (the old
/// <c>&lt;div class="tool-use"&gt;</c> form is still PARSED for legacy history but never emitted). <see cref="Flip"/>
/// is the single mechanism that flips a card to its done form; each card overrides it (and/or
/// <see cref="Render"/>) to add its own custom done behaviour (e.g. a diff card keeps its +/- badges).</summary>
public abstract class Card : ContentBlock
{
    /// <summary>The exact marker string this card was parsed from, if any (round-trip fidelity). When empty,
    /// <see cref="ToString"/> falls back to <see cref="Render"/>.</summary>
    [JsonIgnore]
    public string Marker { get; set; } = "";

    /// <summary>Wire tool name used in this card's markers (read_file, spawn_coder, …).</summary>
    [JsonIgnore]
    protected abstract string ToolName { get; }

    /// <summary>Present- and past-tense verbs, e.g. ("Reading","Read"). Each card supplies its own.</summary>
    [JsonIgnore]
    protected abstract (string Present, string Past) Verbs { get; }

    /// <summary>The label shown after the verb (file, path, pattern, …). Empty for a bare card.</summary>
    [JsonIgnore]
    protected virtual string Label => "";

    /// <summary>The display marker for this card in its CURRENT state — a start marker while Streaming, a done
    /// marker once flipped. This is what the streaming loop appends and, on completion, replaces.</summary>
    public virtual string Render() =>
        $"<!--ari-tool-{(State == State.Complete ? "done" : "start")}:{ToolName}:{MarkerEsc(Label)}-->";

    public override string ToString() => Marker.Length > 0 ? Marker : Render();

    /// <summary>Ends this card's streaming and flips it to its terminal (done) form — past tense at render, plus
    /// any per-card finishing. One-way: an errored card stays Error. Override to add custom done behaviour,
    /// calling base.Flip() first.</summary>
    public virtual void Flip() { if (State != State.Error) State = State.Complete; }

    /// <summary>Flips using the tool's own output, for cards whose done form reports something only the
    /// result knows — how many results a search kept, what a fetched page turned out to be called.
    /// Defaults to the plain flip, so cards that need nothing from the result are unaffected.</summary>
    public virtual void Flip(string result) => Flip();

    protected static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Escapes a label for embedding inside an HTML-comment marker ("--" would close the comment).</summary>
    protected static string MarkerEsc(string s) => s.Replace("--", "&#45;&#45;").Replace(">", "&gt;");

    /// <summary>Populates this card's typed fields from the marker's label.</summary>
    protected internal abstract void Fill(string label);
}

/// <summary>A card that operates on a single file; passes <see cref="FileName"/> down to its children.</summary>
public abstract class FileCard : Card
{
    public string FileName { get; set; } = "";
    protected override string Label => FileName;
    protected internal override void Fill(string label) => FileName = label;
}

public sealed class Reading    : FileCard { protected override string ToolName => "read_file";    protected override (string, string) Verbs => ("Reading",    "Read");      }
public sealed class Previewing : FileCard { protected override string ToolName => "preview_file"; protected override (string, string) Verbs => ("Previewing", "Previewed"); }
public sealed class Deleting   : FileCard { protected override string ToolName => "delete_file";  protected override (string, string) Verbs => ("Deleting",   "Deleted");   }
public sealed class Moving     : FileCard { protected override string ToolName => "move_file";    protected override (string, string) Verbs => ("Moving",     "Moved");     }
public sealed class Reverting  : FileCard { protected override string ToolName => "revert_file";  protected override (string, string) Verbs => ("Reverting",  "Reverted");  }

public sealed class Listing : Card
{
    public string Path { get; set; } = "";
    protected override string Label => Path;
    protected override string ToolName => "list_directory";
    protected override (string, string) Verbs => ("Listing", "Listed");
    protected internal override void Fill(string label) => Path = label;
}

public sealed class Searching : Card
{
    public string Pattern { get; set; } = "";
    protected override string Label => Pattern;
    protected override string ToolName => "search_files";
    protected override (string, string) Verbs => ("Searching", "Searched");
    protected internal override void Fill(string label) => Pattern = label;
}

public sealed class Finding : Card
{
    public string Pattern { get; set; } = "";
    protected override string Label => Pattern;
    protected override string ToolName => "find_files";
    protected override (string, string) Verbs => ("Finding", "Found");
    protected internal override void Fill(string label) => Pattern = label;
}

/// <summary>A web search (search_web). Shows the query as typed so a bad query is visible in the thread,
/// and on completion reports how many results survived the relevance gate — or that none did, or that
/// engines were rate limited. Label encodes the extras as "query|n=7|down=3|none".</summary>
public sealed class WebSearching : Card
{
    public string Query           { get; set; } = "";
    public int    Results         { get; set; } = -1;   // -1 = not known yet (still streaming)
    public int    EnginesDown     { get; set; }
    public bool   NothingRelevant { get; set; }

    protected override string ToolName => "search_web";
    protected override (string, string) Verbs => ("Searching the web", "Searched");
    protected override string Label => Query;

    protected internal override void Fill(string label)
    {
        string[] parts = label.Split('|');
        Query = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if      (p.StartsWith("n=",    StringComparison.Ordinal) && int.TryParse(p[2..], out int n)) Results     = n;
            else if (p.StartsWith("down=", StringComparison.Ordinal) && int.TryParse(p[5..], out int d)) EnginesDown = d;
            else if (p == "none") NothingRelevant = true;
        }
    }

    public override void Flip(string result)
    {
        NothingRelevant = result.StartsWith(SearchWeb.NoRelevantPrefix, StringComparison.Ordinal);

        // Results are numbered "[1] Title" — count the markers rather than re-parsing the whole payload.
        int count = 0;
        foreach (string line in result.Split('\n'))
            if (line.StartsWith('[') && line.Length > 2 && char.IsDigit(line[1])) count++;
        Results = NothingRelevant ? 0 : count;

        Match down = Regex.Match(result, @"\[Search degraded: (\d+) of");
        if (down.Success) EnginesDown = int.Parse(down.Groups[1].Value);

        base.Flip();
    }

    public override string Render()
    {
        if (State != State.Complete) return $"<!--ari-tool-start:{ToolName}:{MarkerEsc(Query)}-->";
        string extra = NothingRelevant ? "|none" : $"|n={Results}";
        if (EnginesDown > 0) extra += $"|down={EnginesDown}";
        return $"<!--ari-tool-done:{ToolName}:{MarkerEsc(Query)}{extra}-->";
    }
}

/// <summary>A page read (fetch_page). Streams as the bare host, then flips to the page's actual title so
/// the thread reads "Read Qwen/Qwen3.6-35B-A3B · huggingface.co" rather than a raw URL. Label encodes
/// the title as "url|t=Title".</summary>
public sealed class Browsing : Card
{
    public string Url   { get; set; } = "";
    public string Title { get; set; } = "";

    protected override string ToolName => "fetch_page";
    protected override (string, string) Verbs => ("Browsing", "Read");
    protected override string Label => Url;

    protected internal override void Fill(string label)
    {
        string[] parts = label.Split('|');
        Url = parts[0];
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].StartsWith("t=", StringComparison.Ordinal)) Title = parts[i][2..];
    }

    public override void Flip(string result)
    {
        // Jina Reader leads with "Title: …"; the Reddit summariser leads with "**title**".
        foreach (string raw in result.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase)) Title = line[6..].Trim();
            else if (line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal) && line.Length > 4)
                Title = line[2..^2].Trim();
            break;
        }
        if (Title.Length > MAX_TITLE_DISPLAY_LENGTH) Title = Title[..MAX_TITLE_DISPLAY_LENGTH].TrimEnd() + "…";
        Title = Title.Replace('|', '-');
        base.Flip();
    }

    public override string Render()
    {
        if (State != State.Complete) return $"<!--ari-tool-start:{ToolName}:{MarkerEsc(Url)}-->";
        string extra = Title.Length > 0 ? $"|t={MarkerEsc(Title)}" : "";
        return $"<!--ari-tool-done:{ToolName}:{MarkerEsc(Url)}{extra}-->";
    }
}

public sealed class Running : Card
{
    public string Command { get; set; } = "";
    protected override string Label => Command;
    protected override string ToolName => "run_command";
    protected override (string, string) Verbs => ("Running", "Ran");
    protected internal override void Fill(string label) => Command = label;
}

/// <summary>Delegation to a Coder sub-agent (spawn_coder). Flips Delegating → Delegated.</summary>
public sealed class Delegating : Card
{
    public string Task { get; set; } = "";
    protected override string Label => Task;
    protected override string ToolName => "spawn_coder";
    protected override (string, string) Verbs => ("Delegating", "Delegated");
    protected internal override void Fill(string label) => Task = label;
}

/// <summary>A project build (build_project). Flips Building → Built.</summary>
public sealed class Building : Card
{
    public string Project { get; set; } = "";
    protected override string Label => Project;
    protected override string ToolName => "build_project";
    protected override (string, string) Verbs => ("Building", "Built");
    protected internal override void Fill(string label) => Project = label;
}

/// <summary>The interactive "plan ready for approval" card (renders an Accept &amp; Build button client-side).</summary>
public sealed class PlanProposed : Card
{
    protected override string ToolName => "plan_proposed";
    protected override (string, string) Verbs => ("Proposing plan", "Plan ready");
    public override string Render() => "<!--ari-plan-proposed-->";
    protected internal override void Fill(string label) { }
}

/// <summary>
/// The interactive "Ari wants to change her persona" card — the diff plus Approve/Reject, rendered inline
/// in the reply that proposed it. The card holds only the proposal id; everything shown is read live from
/// <see cref="PersonaProposalStore"/>, so the moment the user decides, the card in the transcript reflects
/// it without anyone having to mutate the stored response.
/// </summary>
public sealed class PersonaEdit : Card
{
    public string ProposalId { get; set; } = "";

    public string Reason  => Proposal?.Reason  ?? "";
    public string OldText => Proposal?.OldText ?? "";
    public string NewText => Proposal?.NewText ?? "";

    /// <summary>"pending", "approved", "rejected", or "stale". Missing proposal reads as rejected — a card
    /// with nothing behind it must never render an live Approve button.</summary>
    public string Status => Proposal?.Status ?? "rejected";

    private PersonaProposal? Proposal => PersonaProposalStore.Find(ProposalId);

    protected override string ToolName => "propose_persona_edit";
    protected override (string, string) Verbs => ("Proposing a persona change", "Persona change proposed");
    public override string Render() => $"<!--ari-persona-edit:{ProposalId}-->";
    protected internal override void Fill(string label) => ProposalId = label;
}

/// <summary>A light-blue mode-switch info card (e.g. "Returning to planning").</summary>
public sealed class ModeSwitch : Card
{
    public string Text { get; set; } = "";
    protected override string Label => Text;
    protected override string ToolName => "mode";
    protected override (string, string) Verbs => ("", "");
    public override string Render() => $"<!--ari-tool-mode:mode:{MarkerEsc(Text)}-->";
    protected internal override void Fill(string label) => Text = label;
}

/// <summary>A file card that also carries diff stats and an optional patch (edit/write). Renders as the enriched
/// tool-start marker so the client shows the +/- diff badges — the trailing batch-end flips it to the done card,
/// so the diff persists rather than collapsing to a plain label.</summary>
public abstract class DiffCard : FileCard
{
    public int     Added   { get; set; }
    public int     Removed { get; set; }
    public string? Patch   { get; set; }

    public override string Render()
    {
        string diff = "";
        if (Added   > 0) diff += $"|+{Added}";
        if (Removed > 0) diff += $"|-{Removed}";
        return $"<!--ari-tool-start:{ToolName}:{FileName}{diff}-->";
    }

    // Full round-trip: a Complete diff card re-serialises to the start + end + batch markers it was built
    // from, so when its owner's rendered text is re-embedded and re-parsed (Coder work → architect response)
    // the pairing re-forms and the card stays Complete instead of collapsing back to a live "Editing…".
    public override string ToString()
    {
        if (State != State.Complete) return Render();
        string diff = "";
        if (Added   > 0) diff += $"|+{Added}";
        if (Removed > 0) diff += $"|-{Removed}";
        string patch = string.IsNullOrEmpty(Patch) ? "" : $"|{Patch}";
        return $"<!--ari-tool-start:{ToolName}:{FileName}{diff}-->" +
               $"<!--ari-tool-end:{ToolName}:{FileName}{diff}{patch}--><!--ari-batch-end-->";
    }

    // Label form: "File|+12|-5|<base64 patch>" (counts and patch optional).
    protected internal override void Fill(string label)
    {
        string[] parts = label.Split('|');
        FileName = parts.Length > 0 ? parts[0] : "";
        foreach (string part in parts.Skip(1))
        {
            if      (part.StartsWith('+') && int.TryParse(part[1..], out int add)) Added   = add;
            else if (part.StartsWith('-') && int.TryParse(part[1..], out int del)) Removed = del;
            else if (part.Length > 0)                                             Patch   = part;
        }
    }
}

public sealed class Editing : DiffCard { protected override string ToolName => "edit_file";  protected override (string, string) Verbs => ("Editing", "Edited"); }
public sealed class Writing : DiffCard { protected override string ToolName => "write_file"; protected override (string, string) Verbs => ("Writing", "Wrote");  }

/// <summary>An anchor that says "render child thread <see cref="ChildKey"/> here" inside a parent Response.
/// The child keeps its own <see cref="Thread"/> and history (so its context never contaminates the parent);
/// for DISPLAY only, the owning Response splices the child's blocks into <see cref="Blocks"/> at serialization.
/// Contributes nothing to the parent's LLM context — isolation is structural, not flag-based.</summary>
public sealed class Subthread : ContentBlock
{
    public string ChildKey { get; set; } = "";
    public string Label    { get; set; } = "";

    /// <summary>The child thread's display blocks, spliced in by the owning Response (which holds the
    /// key→Thread map). Empty until resolved — the child's real content lives on its own thread.</summary>
    [JsonPropertyName("blocks")]
    public List<ContentBlock> Blocks { get; set; } = new();

    // Round-trips through the stream string as a bare anchor marker; the child is never inlined into the string.
    public override string ToString() => $"<!--ari-subthread:{ChildKey}|{Label}-->";
}

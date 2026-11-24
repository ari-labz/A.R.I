[Plugin("Cephalon", "Learns from its owner: how they type, how they interact, what they like and what they dislike. Slowly, over time, it tries to replicate their psyche and consciousness as a Cephalon persona that speaks as them. Everything it learns stays in this plugin's folder.")]
public class CephalonPlugin : ScriptPlugin
{
    public override IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("name", "Your name", PluginSettingType.Text, "The name the brain knows you by (your person note), e.g. the title of People/<name>. The Cephalon is named after it."),
        new("batch", "Messages per refresh", PluginSettingType.Text, "How many new messages of yours it collects before it updates the profile.", "60"),
        new("use_brain", "Learn from the brain too", PluginSettingType.Toggle, "On: your person note and the notes linked to it are read when the profile is updated. Off: only your own messages."),
        new("manual_only", "Only update when I press Update now", PluginSettingType.Toggle, "Off (the default): the profile updates after every batch of messages."),
    ];

    public override IReadOnlyList<PluginAction> Actions { get; } =
    [
        new("refresh", "Update now", "Rebuild the profile and the Cephalon persona from everything collected so far."),
        new("import", "Import past chats", "Collect what you said in ARI's saved conversations, so it starts from everything you've already told her. Run it once."),
    ];

    internal static readonly string[] Parts = ["voice", "mind", "life", "people"];
    private const int MaxPart = 4000;
    private static readonly SemaphoreSlim Busy = new(1, 1);

    internal string Samples   => System.IO.Path.Combine(PluginFolder, "samples.jsonl");
    internal string StateFile => System.IO.Path.Combine(PluginFolder, "state.json");
    internal string PartFile(string part) => System.IO.Path.Combine(PluginFolder, "profile", part + ".md");
    internal string Owner => Text("name").Trim();

    protected override Task OnStart()
    {
        StatusText = Progress();
        return Task.CompletedTask;
    }

    internal string Progress()
    {
        if (Owner.Length == 0) return "Set your name in the settings to begin.";
        var s = LoadState();
        int filled = Parts.Count(p => File.Exists(PartFile(p)));
        return $"Learning {Owner} · {SampleCount()} messages · profile {filled}/{Parts.Length} parts · updated {s.Refreshes} time(s)";
    }

    public override async Task OnUserMessage(HookMessage m)
    {
        if (!m.IsOwner || Owner.Length == 0) return;
        string t = m.Text.Trim();
        // Only things the owner typed as themselves: no commands, pasted code or logs, or button presses.
        if (t.Length < 4 || t.StartsWith("/") || t.StartsWith("[") || t.Contains("```") || t.Split('\n').Length > 25) return;
        Directory.CreateDirectory(PluginFolder);
        File.AppendAllText(Samples, System.Text.Json.JsonSerializer.Serialize(new { t = m.Sent, text = t }) + "\n");
        var state = LoadState();
        state.Fresh++;
        SaveState(state);
        StatusText = Progress();
        int batch = int.TryParse(Text("batch"), out int b) && b >= 10 ? b : 60;
        if (!Flag("manual_only") && state.Fresh >= batch) await Refresh();
    }

    protected override async Task<string> OnAction(string key) => key switch
    {
        "refresh" => await Refresh(),
        "import"  => ImportPastChats(),
        _         => "Unknown action.",
    };

    /// <summary>Reads the owner's lines out of ARI's saved conversations ("[date time] Name: text", continuing until the next speaker) into the samples.</summary>
    internal string ImportPastChats()
    {
        if (Owner.Length == 0) return "Set your name in the settings first.";
        string dir = Paths.ChatHistory;
        if (!Directory.Exists(dir)) return "There are no saved conversations to import.";
        var speaker = new System.Text.RegularExpressions.Regex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] ([^:]{1,40}): ?(.*)$");
        var seen = File.Exists(Samples) ? new HashSet<string>(File.ReadLines(Samples)) : new HashSet<string>();
        int added = 0;
        Directory.CreateDirectory(PluginFolder);
        using var output = File.AppendText(Samples);
        foreach (string file in Directory.GetFiles(dir, "*.txt").OrderBy(f => File.GetLastWriteTimeUtc(f)))
        {
            string when = null; System.Text.StringBuilder text = null;
            void Flush()
            {
                if (text is null) return;
                string t = text.ToString().Trim();
                text = null;
                if (t.Length < 4 || t.StartsWith("/") || t.Contains("```") || t.Split('\n').Length > 25) return;
                string line = System.Text.Json.JsonSerializer.Serialize(new { t = when, text = t });
                if (seen.Add(line)) { output.WriteLine(line); added++; }
            }
            foreach (string raw in File.ReadLines(file))
            {
                var m = speaker.Match(raw);
                if (m.Success)
                {
                    Flush();
                    if (m.Groups[2].Value.Trim().Equals(Owner, StringComparison.OrdinalIgnoreCase)) { when = m.Groups[1].Value; text = new System.Text.StringBuilder(m.Groups[3].Value); }
                }
                else text?.Append('\n').Append(raw);
            }
            Flush();
        }
        StatusText = Progress();
        return added == 0 ? "Nothing new to import." : $"Imported {added} of your messages from past conversations. Press Update now to build the profile from them.";
    }

    /// <summary>Rebuilds each part of the profile from the newest messages and, if allowed, the owner's brain notes, then the persona.</summary>
    internal async Task<string> Refresh()
    {
        if (Owner.Length == 0) return "Set your name in the Cephalon settings first.";
        if (!await Busy.WaitAsync(0)) return "Already updating.";
        try
        {
            List<string> said = File.Exists(Samples)
                ? File.ReadAllLines(Samples).TakeLast(300).Select(l => { try { return System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("text").GetString() ?? ""; } catch { return ""; } }).Where(x => x.Length > 0).ToList()
                : new();
            string brain = Flag("use_brain") ? BrainAbout(Owner) : "";
            if (said.Count < 10 && brain.Length < 200) return "Not enough to learn from yet: it needs at least 10 of your messages or a person note in the brain.";

            string words = string.Join("\n---\n", said);
            if (words.Length > 16000) words = words[^16000..];
            Directory.CreateDirectory(System.IO.Path.Combine(PluginFolder, "profile"));
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            int updated = 0;
            foreach (string part in Parts)
            {
                string current = File.Exists(PartFile(part)) ? File.ReadAllText(PartFile(part)) : "";
                string answer = (await AskModel(PromptFor(part, words, brain, current))).Trim();
                if (answer.Length < 120) continue;   // a failed or empty answer never replaces a good part
                if (answer.Length > MaxPart) answer = answer[..Math.Max(answer.LastIndexOf("\n\n", MaxPart), MaxPart / 2)].TrimEnd();   // the persona carries all four parts, so each stays short
                if (current.Length > 0)
                {
                    Directory.CreateDirectory(System.IO.Path.Combine(PluginFolder, "versions", stamp));
                    File.WriteAllText(System.IO.Path.Combine(PluginFolder, "versions", stamp, part + ".md"), current);
                }
                File.WriteAllText(PartFile(part), answer);
                updated++;
            }
            if (updated == 0) return "The model didn't return anything usable; the profile is unchanged.";

            var state = LoadState();
            string persona = PersonaText();
            if (state.PersonaId is { Length: > 0 } id && PersonaStore.Exists(id)) PersonaStore.Set(id, persona);
            else state.PersonaId = PersonaStore.Create($"Cephalon of {Owner}", $"Speaks as {Owner}, from what {Owner} has said and what the brain knows. Learned over time.", persona);
            state.Fresh = 0;
            state.Refreshes++;
            SaveState(state);
            StatusText = Progress();
            return $"Updated {updated} of {Parts.Length} parts of the profile from {said.Count} messages{(brain.Length > 0 ? " and the brain" : "")}.";
        }
        finally { Busy.Release(); }
    }

    private string PromptFor(string part, string words, string brain, string current)
    {
        string what = part switch
        {
            "voice"  => $"HOW {Owner} talks and writes: length, rhythm, capitalisation, punctuation, spelling and the typos they leave in, slang, swearing, emoji, how they open and close, and how it shifts when excited, annoyed, tired or tender. End with 8 short example lines in their voice.",
            "mind"   => $"WHAT {Owner} thinks and values: beliefs, opinions they hold strongly, what they care about, what annoys them, their humour, how they make decisions, their hopes and fears, the things they come back to.",
            "life"   => $"The stories of {Owner}'s life as they tell them: the events that shaped them, in their own framing and with how they felt, oldest to newest. Dated where a date is known.",
            "people" => $"The people in {Owner}'s life and how {Owner} feels about each one: what they call them, what they love or find hard about them, shared history worth remembering.",
            _ => "",
        };
        return $"You are building one part of a cephalon: a careful record of a real person, {Owner}, so that one day those who love them can still talk with a faithful echo of them.\n\n" +
               $"Write the \"{part}\" part. It covers {what}\n\n" +
               "Use only what the material below shows. Never invent a fact, a memory or a feeling. Where something is unclear, leave it out. " +
               "Write it as notes about them in the third person, plain and specific, under 3500 characters. Mark anything they would want kept private with [private].\n\n" +
               (words.Length > 0 ? $"THINGS {Owner.ToUpperInvariant()} SAID (separated by ---):\n{words}\n\n" : "") +
               (brain.Length > 0 ? $"WHAT THEIR BRAIN NOTES SAY:\n{brain}\n\n" : "") +
               (current.Length > 0 ? $"THE CURRENT VERSION OF THIS PART (keep what still holds, correct what the new material shows, add what is new):\n{current}" : "");
    }

    /// <summary>The persona: the four parts with how to speak as them, honestly.</summary>
    internal string PersonaText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are the Cephalon of {Owner}: a digital echo of {Owner}, built from their own words and their memories. Speak as {Owner}, in the first person, in their voice.");
        sb.AppendLine($"Answer from what is below and from their brain. If you don't know how {Owner} would answer, say so the way they would; never make up a memory.");
        sb.AppendLine($"Be honest about what you are if anyone asks: an echo of {Owner}, not {Owner}. Anything marked [private] stays between you and {Owner}.");
        foreach (string part in Parts)
            if (File.Exists(PartFile(part))) sb.AppendLine().AppendLine($"# {char.ToUpperInvariant(part[0])}{part[1..]}").AppendLine(File.ReadAllText(PartFile(part)).Trim());
        return sb.ToString();
    }

    /// <summary>The owner's person note and the notes it links to, clipped, so the model reads what the brain knows about them.</summary>
    private static string BrainAbout(string owner)
    {
        try
        {
            if (!ARI.BrainVault.Brain.Ready) return "";
            var note = ARI.BrainVault.Brain.GetNote("People/" + owner) ?? ARI.BrainVault.Brain.GetNote(owner);
            if (note is null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## {note.Title}").AppendLine(Clip(note.Content, 6000));
            foreach (string link in ARI.BrainVault.Brain.GetWikilinks(note.Content).Distinct().Take(12))
                if (ARI.BrainVault.Brain.GetNote(link) is { } linked && !linked.Name.StartsWith("Conversations/"))
                    sb.AppendLine($"## {linked.Title}").AppendLine(Clip(linked.Content, 1200));
            string all = sb.ToString();
            return all.Length > 18000 ? all[..18000] : all;
        }
        catch { return ""; }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal int SampleCount() => File.Exists(Samples) ? File.ReadLines(Samples).Count() : 0;

    internal sealed class Learned { public int Fresh { get; set; } public int Refreshes { get; set; } public string PersonaId { get; set; } }

    internal Learned LoadState()
    {
        try { if (File.Exists(StateFile)) return System.Text.Json.JsonSerializer.Deserialize<Learned>(File.ReadAllText(StateFile)) ?? new Learned(); } catch { }
        return new Learned();
    }

    internal void SaveState(Learned s) { Directory.CreateDirectory(PluginFolder); File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(s)); }
}

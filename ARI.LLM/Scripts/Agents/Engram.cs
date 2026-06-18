using ARI.Common;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ARI.Brain;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class Engram : Agent, IDisposable
{
    private const int ENGRAM_TRIGGER_DELAY = 5;

    [JsonIgnore] internal Dialogue?    dialogue       { get; set; }
    [JsonIgnore] internal BrainModule? brain          { get; set; }
    [JsonIgnore] internal Context?     context        { get; set; }
    [JsonIgnore] internal string       brainPublicUrl { get; set; } = "";

    private EngramBuffer? buffer;

    [JsonPropertyName("recursiveBrainSearchDepth")] public int RecursiveBrainSearchDepth { get; init; } = 7;
    [JsonPropertyName("sweepIntervalMinutes")]      public int SweepIntervalMinutes       { get; init; }

    private readonly Dictionary<string, DateTime>       lastRun          = new();
    private readonly Dictionary<string, int>            lastHistoryCount = new();
    private readonly SemaphoreSlim                      engramLock       = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> sweepingThreads  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient                         httpClient       = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    internal event Action<string>? SweepCompleted;

    internal bool IsSweeping(string threadKey) => sweepingThreads.ContainsKey(threadKey);

    internal async Task WaitForSweep(string threadKey, CancellationToken ct)
    {
        if (!IsSweeping(threadKey)) return;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string>? handler = null;
        handler = key =>
        {
            if (!key.Equals(threadKey, StringComparison.OrdinalIgnoreCase)) return;
            SweepCompleted -= handler;
            tcs.TrySetResult(true);
        };
        SweepCompleted += handler;
        if (!IsSweeping(threadKey)) { SweepCompleted -= handler; return; }

        using CancellationTokenRegistration reg = ct.Register(() => { SweepCompleted -= handler; tcs.TrySetCanceled(ct); });
        await tcs.Task;
    }

    internal override ThreadType Type => ThreadType.Engram;

    internal Engram() { }

    internal void Init(Dialogue dialogue, BrainModule brain, Context? context, string brainPublicUrl = "")
    {
        this.dialogue       = dialogue;
        this.brain          = brain;
        this.context        = context;
        this.brainPublicUrl = brainPublicUrl;

        buffer = new EngramBuffer(dialogue, this);

        dialogue.ThreadBufferFull += threadKey =>
        {
            buffer.Remove(threadKey);
            _ = Task.Delay(TimeSpan.FromSeconds(ENGRAM_TRIGGER_DELAY)).ContinueWith(_ => RunEngram(threadKey, "chat buffer"));
        };

        dialogue.ThreadDeleted += threadKey =>
        {
            lastRun.Remove(threadKey);
            lastHistoryCount.Remove(threadKey);
            buffer.Remove(threadKey);
        };
    }

    internal bool IsEnabled { get; private set; } = true;

    internal void Enable()
    {
        IsEnabled = true;
        Shared.Logger.LogInformation("[Engram] Enabled.");
    }

    internal void Disable()
    {
        IsEnabled = false;
        Shared.Logger.LogInformation("[Engram] Disabled.");
    }

    internal Task<int> PurgeNotes() => brain.PurgeAllNotes();

    public void Dispose()
    {
        engramLock.Dispose();
        httpClient.Dispose();
    }

    internal async Task RunEngram(string threadKey, string trigger)
    {
        if (!IsEnabled) return;
        if (!await engramLock.WaitAsync(0)) return;
        sweepingThreads[threadKey] = 0;
        try
        {
            List<ThreadItem> allItems = dialogue.GetThread(threadKey)?.History ?? new List<ThreadItem>();
            List<ThreadItem> conversationItems = allItems.Where(i => i is UserMessage or AriResponse).ToList();

            int lastCount = lastHistoryCount.TryGetValue(threadKey, out int c) ? c : 0;
            List<ThreadItem> recentItems = conversationItems.Skip(lastCount).ToList();

            lastRun[threadKey]          = DateTime.UtcNow;
            lastHistoryCount[threadKey] = conversationItems.Count;

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep triggered (trigger: {Trigger})", threadKey, trigger);

            // --- Phase 1: Classify ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 1 — classifying conversation...", threadKey);
            if (!await Classify(recentItems, trigger)) return;

            // --- Phase 2: Fetch ---
            List<string> existingNotes  = await brain.GetNotePaths();
            Dictionary<string, List<string>> aliasesByTitle = brain.GetAliasesByTitle();
            string existingNotesList    = existingNotes.Count > 0
                ? string.Join(", ", existingNotes.Select(p =>
                  {
                      string bare = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
                      return aliasesByTitle.TryGetValue(bare, out List<string>? al) && al.Count > 0
                          ? $"{p} (aka: {string.Join(", ", al)})"
                          : p;
                  }))
                : "none";
            string transcript           = BuildTranscript(conversationItems);
            string engramThreadKey      = $"engram:{Guid.NewGuid()}";

            if (context is not null)
                await context.RebuildFromTranscript(threadKey, transcript);

            string contextSummary = context?.GetContext(threadKey) ?? string.Empty;

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 2 — fetch (aware of {Count} existing note(s) with full paths)",
                threadKey, existingNotes.Count);

            string contextBlock = string.IsNullOrWhiteSpace(contextSummary)
                ? string.Empty
                : $"CONTEXT SUMMARY (use this to resolve all pronouns and identify topics):\n{contextSummary}\n\n";

            HashSet<string> alreadyFetched = new(StringComparer.OrdinalIgnoreCase);

            string initialFetchPrompt =
                "Analyse this conversation and the list of existing notes.\n\n" +
                contextBlock +
                $"CONVERSATION:\n{transcript}\n\n" +
                $"EXISTING NOTES (full paths — the path encodes category, ownership, and relationship): {existingNotesList}\n\n" +
                "An entity already in this list — under its title OR any of its '(aka: ...)' aliases — is the SAME entity; plan to update it, never to create a second note for it.\n" +
                "Use the paths to understand the graph structure before fetching. " +
                "Identify any notes you want to read — to check for duplicates and to update existing notes. " +
                "Any note you intend to update must be fetched first.\n" +
                "Respond with bare note TITLES (the last segment of the path): {\"fetch\": [\"[REDACT]\"]} — or {\"fetch\": []} to proceed straight to extraction.";

            string initialRaw    = await SendPrompt(engramThreadKey, initialFetchPrompt);
            List<string> toFetch = ParseFetchList(initialRaw).Where(n => !alreadyFetched.Contains(n)).ToList();

            if (toFetch.Count == 0)
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] fetch round 1: no notes requested, proceeding to plan.", threadKey);

            for (int depth = 0; depth < RecursiveBrainSearchDepth && toFetch.Count > 0; depth++)
            {
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] fetch round {Round}: requesting [{Notes}]",
                    threadKey, depth + 1, string.Join(", ", toFetch));

                StringBuilder sb = new();
                foreach (string name in toFetch)
                {
                    string? noteContent = await brain.GetNote(name);
                    if (noteContent is null) continue;
                    alreadyFetched.Add(name);
                    sb.AppendLine($"--- {name} ---");
                    sb.AppendLine(noteContent);
                    sb.AppendLine("---");
                }

                if (sb.Length == 0) break;

                bool atLimit = depth + 1 >= RecursiveBrainSearchDepth;
                string deliverPrompt = atLimit
                    ? $"Here are the notes you requested:\n\n{sb}\n\n(Fetch limit reached — proceeding to planning.)"
                    : $"Here are the notes you requested:\n\n{sb}\n\n" +
                      $"Already fetched: {string.Join(", ", alreadyFetched)}.\n" +
                      "If any of those notes reference further notes you need to read (e.g. a [[link]]), request them now. " +
                      "Respond with {\"fetch\": [\"Name\"]} to request more, or {\"fetch\": []} to proceed to planning.";

                string deliverRaw = await SendPrompt(engramThreadKey, deliverPrompt);
                toFetch = atLimit
                    ? new List<string>()
                    : ParseFetchList(deliverRaw).Where(n => !alreadyFetched.Contains(n)).ToList();

                if (toFetch.Count == 0)
                    Shared.Logger.LogInformation("[Engram] [{ThreadKey}] fetch round {Round}: no further notes requested.", threadKey, depth + 2);
            }

            if (alreadyFetched.Count > 0)
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] fetch complete: read {Count} note(s): [{Notes}]",
                    threadKey, alreadyFetched.Count, string.Join(", ", alreadyFetched));

            // --- Phase 3: Plan ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 3 — planning changes...", threadKey);

            string contextPreamble = string.IsNullOrWhiteSpace(contextSummary)
                ? string.Empty
                : $"Use the context summary to resolve all pronouns:\n{contextSummary}\n\n";

            string planPrompt =
                contextPreamble +
                "Based on the conversation and the notes you have read, list every note you intend to create or edit.\n\n" +
                "For each note provide:\n" +
                "- op: \"add\" for a new note, \"edit\" for updating a note you have already fetched\n" +
                "- name: the DESIRED full note path — this is where the note WILL live after the sweep\n" +
                "- summary: 1–2 sentences — the key facts, pronouns, and main links this note will contain\n" +
                "- newName (optional): only when an existing note needs to MOVE. Set name to the CURRENT path, newName to the TARGET path.\n\n" +

                "## PATH IS TAXONOMY\n" +
                "The path encodes full meaning before the note is even opened. Each segment should answer: what is this, whose is it, how does it relate?\n" +
                "- A grandparent note sits at: People/[Person]'s Family/Immediate Family/Grandparents/[Name]\n" +
                "- A cousin sits at: People/[Person]'s Family/Cousins/[Name]\n" +
                "- A job sits at: People/[Person]/Employment/[Company]\n" +
                "- An event sits at: Events/[Event Name]\n" +
                "Do not flatten to two levels just for simplicity. Use as many levels as needed to place the entity correctly in the graph.\n\n" +

                "## ENTITIES AS FOLDERS\n" +
                "When a person or project has multiple distinct facets worth noting, they become both a note AND a folder root.\n" +
                "- The person note lives at: People/[Name]\n" +
                "- Sub-topics nest under them: People/[Name]/Employment/[Company], People/[Name]/Goals, etc.\n" +
                "- Projects work the same: Projects/[Name] (note) + Projects/[Name]/[Sub-component] (child notes)\n\n" +

                "## HUB NOTES\n" +
                "Any grouping benefits from a hub note that indexes and summarises the notes within it. Hubs exist at every level:\n" +
                "- A family hub at: People/[Person]'s Family\n" +
                "- A sub-group hub at: People/[Person]'s Family/Cousins\n" +
                "- A thematic hub that cuts across folders: Projects/[Person]'s Tech (linking Hardware + Software + Projects)\n" +
                "Hub notes are named possessively when they belong to a person ([Person]'s Family, [Person]'s Friends).\n" +
                "This makes them unambiguous across multiple people's graphs.\n" +
                "Individual spokes link TO the hub; the hub links down to members — not the reverse.\n" +
                "A hub links DOWN to EVERY one of its direct children, including children that are themselves hubs: '[REDACT]'s Family' links to 'Immediate Family', 'Cousins', and 'Grandparents'. Link to direct children only — each sub-hub links to its own members. Do not merge a sub-hub into its parent; keep the nesting and link to it.\n\n" +

                "## ONE ENTITY, ONE NOTE\n" +
                "A title is an identity — the graph allows exactly one note per entity. Before planning an 'add', check the existing notes (including their '(aka: ...)' aliases) for the same person/place/thing under ANY name. If it already exists, plan an 'edit' on that note instead — never a second note. A nickname, role, or formal-name variant is the same entity, not a new one.\n\n" +

                "## NO DEAD ENDS\n" +
                "Recall finds a note, then follows that note's OWN outward [[links]] to reach what is related — inbound links are invisible to it. So every note must link outward to its hub (a family member links to its family hub; a device links to the owner's tech hub). A note with no outward link can be found but leads nowhere.\n\n" +

                "## DON'T OVER-CONNECT\n" +
                "Route links through hubs: a person links to their HUBS (Family, Friends, Romantic Partners, Tech), not directly to every individual member. Every link needs a reason — membership, the subject of a fact, or hub indexing. Do not link things that merely co-occur (a laptop does not link to a friend). Links are one-way: if A mentions B, only A links to B.\n\n" +

                "## RELATIONSHIPS\n" +
                "The dynamics of a relationship between two people belong in Relationships/, not duplicated on each person's note.\n" +
                "- Use: Relationships/[Person A] and [Person B] Relationship\n" +
                "- Descriptors like 'long distance', 'estranged', 'close' are STATUSES — write them as a field or sentence inside the relationship note, never as a separate note.\n\n" +

                "## EVENT NOTES\n" +
                "Notes in Events/ are point-in-time snapshots. Each event note:\n" +
                "- Must carry a specific or approximate date, either in the title or as the first prominent fact.\n" +
                "- Records what happened at that moment: who was involved, where, what occurred, how it felt.\n" +
                "- Links outward to ongoing notes for broader context (e.g. an event note about a first date links to the Relationships/ note for the ongoing relationship).\n" +
                "- Does NOT carry evolving facts — those belong in the linked ongoing note.\n" +
                "Example: 'Events/[REDACT] and [REDACT] got together' captures the date and the occasion. The ongoing story lives in 'Relationships/[REDACT] and [REDACT] Relationship'.\n\n" +

                "## CONVERSATION NOTES\n" +
                "Maintain exactly ONE dated log note per day at Conversations/YYYY-MM-DD, dated to when the conversation happened (use today's date from the context summary).\n" +
                "- It is a temporal INDEX, not a fact store: a 1–3 sentence summary of what was discussed that day, followed by a [[link]] to every person, place, project, or topic that came up.\n" +
                "- If today's note already exists, EDIT it (op \"edit\"): append the new summary line and add links for any newly-discussed notes. NEVER create a second note for a day that already has one.\n" +
                "- Do NOT store evolving facts here — those live on each entity's own note. The conversation note only records that a topic was discussed on this date and links to it, so 'what did we talk about on YYYY-MM-DD' is answerable.\n" +
                "- Only link to notes that exist or are being created this sweep.\n\n" +

                "## DO NOT CREATE NOTES FOR DESCRIPTORS\n" +
                "A descriptor, status, or label is not a note. It belongs as a field, sentence, or section inside the relevant note.\n" +
                "Wrong: a note titled 'Long Distance Relationship' that describes a status.\n" +
                "Right: a 'Current Status: Long distance' field inside 'Relationships/[REDACT] and [REDACT] Relationship'.\n" +
                "Other examples that should NOT be standalone notes: 'Employed', 'Student', 'Estranged', 'Deceased'.\n\n" +

                "## NOTE TITLES ARE COMMON NAMES\n" +
                "A note's title must be the name used in everyday speech — the nickname, alias, or preferred name — not the formal or legal name.\n" +
                "The formal name belongs inside the note (e.g. under ## Info as **Full Name:** ...). Use newName to rename if needed.\n" +
                "This is critical for recall: if someone is always called 'Grumpy', a note titled 'Geoffrey' will never match when that name is spoken.\n" +
                "It also prevents duplicates: a new note 'Andi' and an existing note '[REDACT]' are the same person — catch this by checking existing notes for aliases.\n" +
                "Only rename when the preferred name is clearly known. If uncertain, leave the title unchanged.\n\n" +

                "## DISAMBIGUATION\n" +
                "When two distinct things share the same name, append a parenthetical descriptor to each, Wikipedia-style.\n" +
                "Example: a boat and a person both named 'Granny Squeak' become 'Granny Squeak (boat)' and 'Granny Squeak (person)'.\n" +
                "Apply this whenever a collision exists or would be caused by a rename. The parenthetical should be the shortest phrase that makes the note unambiguous.\n\n" +

                "## EVENTS MUST HAVE DATES\n" +
                "Every entry in an Events section must carry a specific or approximate date. Never use relative time ('several years ago', 'recently', 'last year') — these rot as time passes.\n" +
                "- Specific date: '25th August 2024: Met [REDACT] on VRChat.'\n" +
                "- Approximate date: '~May 2026: Bought a house.'\n" +
                "- Known year only: '2023: Started university.'\n" +
                "If a date cannot be determined at all, describe the event inline in the note body rather than listing it under Events.\n\n" +

                "## CHANGELOG\n" +
                "Every note you create or edit must include a ## Changelog section.\n" +
                "Add a dated entry describing what was added or changed: '- 2026-06-02: Created note. Added family and residence info.'\n" +
                "Do NOT include [[links]] in changelog entries — plain text only. Changelog entries are a prose audit trail, not part of the graph.\n\n" +

                "## LINKS MUST EXIST\n" +
                "Only write [[links]] to notes that already exist or that you are creating in this same sweep.\n" +
                "Do NOT write a bullet point whose only content is a [[link]] to a note that does not exist — omit the bullet entirely.\n\n" +

                "Include any Unknown/ notes that should be moved to a proper category (use newName).\n" +
                "Include today's Conversations/YYYY-MM-DD note (see CONVERSATION NOTES above) — edit it if it already exists, otherwise add it.\n\n" +

                "Output ONLY:\n" +
                "{\"plan\": [{\"op\": \"add\", \"name\": \"People/[Person]'s Family/Immediate Family/[Name]\", \"summary\": \"...\"}]}\n" +
                "If nothing needs to be stored: {\"plan\": []}";

            string planRaw = await SendPrompt(engramThreadKey, planPrompt);
            List<EngramPlanItem> plan = ParsePlanManifest(planRaw);

            if (plan.Count == 0)
            {
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] plan is empty — nothing to store.", threadKey);
                return;
            }

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] plan: {Count} change(s) — [{Notes}]",
                threadKey, plan.Count, string.Join(", ", plan.Select(p =>
                    string.IsNullOrWhiteSpace(p.NewName)
                        ? $"{p.Name} ({p.Op})"
                        : $"{p.Name} → {p.NewName} ({p.Op})")));

            // Snapshot the engram thread's context at this point so each per-note write forks from it.
            IReadOnlyList<ThreadMessage> savedContext = Threads.TryGetValue(engramThreadKey, out Thread? engramThread)
                ? ContextSnapshot(engramThread)
                : Array.Empty<ThreadMessage>();

            // --- Phase 4: Write notes one at a time ---
            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 4 — writing {Count} note(s)...", threadKey, plan.Count);

            StringBuilder sweepSummary = new();
            int successCount = 0;
            int failCount    = 0;
            List<NoteChange> queueChanges = new();

            for (int i = 0; i < plan.Count; i++)
            {
                EngramPlanItem item = plan[i];
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] writing ({Current}/{Total}): {Name} ({Op})",
                    threadKey, i + 1, plan.Count, item.Name, item.Op);

                string moveInstruction = string.IsNullOrWhiteSpace(item.NewName)
                    ? string.Empty
                    : $" Move it from its current path to {item.NewName} by setting newName accordingly.";

                string writePrompt =
                    (sweepSummary.Length > 0 ? $"Notes already saved this sweep:\n{sweepSummary}\n" : "") +
                    $"Now write the note: {item.Name}.{moveInstruction}\n\n" +
                    "Rules for this note:\n" +
                    "- Title must be the everyday name (nickname/alias), NEVER a role or status — '[REDACT] (Boyfriend)' is wrong; the title is '[REDACT]' and the role goes in the body. Formal name goes inside under ## Info and into aliases.\n" +
                    "- If the title would collide with another note's name, append a parenthetical to disambiguate (e.g. 'Granny Squeak (person)' vs 'Granny Squeak (boat)').\n" +
                    "- Every Events entry must have a specific or approximate date (e.g. '25th August 2024:' or '~May 2026:'). Never write relative time ('recently', 'several years ago').\n" +
                    "- Include a ## Changelog section. Add a dated entry for what was created or changed. No [[links]] in changelog — plain text only.\n" +
                    "- Only [[link]] to notes that exist or are being created this sweep.\n" +
                    "- Include an \"aliases\" array with every nickname, callsign, shortened name, or alternate name that someone might speak aloud to refer to this entity. " +
                    "These are stored as searchable labels so that saying 'Grumpy' surfaces 'Geoffrey'. " +
                    "Only include names actually mentioned or clearly implied — do not guess. Empty array if none.\n\n" +
                    "Output a single JSON object in this exact format:\n" +
                    "For a new note:    {\"add\": [{\"name\": \"Category/SubGroup/NoteName\", \"content\": \"markdown\", \"aliases\": [\"Nickname\", \"AltName\"]}], \"edit\": []}\n" +
                    "For an update:     {\"add\": [], \"edit\": [{\"name\": \"CurrentPath\", \"newName\": \"NewPath\", \"content\": \"full markdown\", \"aliases\": [\"Nickname\"]}]}\n" +
                    "Paths may be as deep as the taxonomy requires — e.g. \"People/[REDACT]'s Family/Immediate Family/Grandparents/Geoffrey\".\n" +
                    "(Omit newName if the path is not changing. Raw JSON only — no fences, no explanation.)";

                // Fork from the saved context snapshot so each write call starts with the same base.
                Thread writeThread = new Thread(Type, $"adhoc:{Guid.NewGuid()}");
                writeThread.Seed(savedContext);
                string writeRaw = await SendPrompt(writeThread, writePrompt, maxTokensOverride: -1);

                (List<EngramAdd> noteAdds, List<EngramEdit> noteEdits) = ParseEngramOutput(writeRaw);

                string plannedName = item.Name;
                noteAdds  = noteAdds .Where(a => a.NoteName.Equals(plannedName, StringComparison.OrdinalIgnoreCase)).ToList();
                noteEdits = noteEdits.Where(e => e.NoteName.Equals(plannedName, StringComparison.OrdinalIgnoreCase)).ToList();

                if (noteAdds.Count == 0 && noteEdits.Count == 0)
                {
                    Shared.Logger.LogError("[Engram] [{ThreadKey}] failed to parse note ({Current}/{Total}): {Name}. Raw response: {Raw}",
                        threadKey, i + 1, plan.Count, item.Name, writeRaw);
                    failCount++;
                    continue;
                }

                if (noteAdds.Count  > 0) await brain.AddNotes(noteAdds);
                if (noteEdits.Count > 0) await brain.EditNotes(noteEdits);

                static string BareName(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
                IEnumerable<string> writtenTitles = noteAdds.Select(a => BareName(a.NoteName))
                    .Concat(noteEdits.Select(e => BareName(e.NewNoteName ?? e.NoteName)));
                await brain.MarkDirty(writtenTitles);

                foreach (EngramAdd add in noteAdds)
                {
                    string  title  = BareName(add.NoteName);
                    string? noteId = await brain.GetNoteId(title);
                    string? url    = noteId is not null && !string.IsNullOrEmpty(brainPublicUrl)
                        ? $"{brainPublicUrl}/#root/{noteId}" : null;
                    queueChanges.Add(new NoteChange(title, url, "created", "created"));
                }
                foreach (EngramEdit edit in noteEdits)
                {
                    string  title  = BareName(edit.NewNoteName ?? edit.NoteName);
                    string? noteId = await brain.GetNoteId(title);
                    string? url    = noteId is not null && !string.IsNullOrEmpty(brainPublicUrl)
                        ? $"{brainPublicUrl}/#root/{noteId}" : null;
                    queueChanges.Add(new NoteChange(title, url, "updated", item.Summary));
                }

                sweepSummary.AppendLine($"- {item.Name}: {item.Summary}");
                successCount++;

                string savedName = noteAdds.Count > 0 ? noteAdds[0].NoteName : noteEdits[0].NoteName;
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] saved ({Current}/{Total}): {Name}",
                    threadKey, i + 1, plan.Count, savedName);
            }

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] phase 4 complete: {Success} saved, {Fail} failed.",
                threadKey, successCount, failCount);

            if (queueChanges.Count > 0)
                Shared.Logger.LogInformation("[Engram] [{ThreadKey}] {Count} note change(s): {Changes}",
                    threadKey, queueChanges.Count, string.Join(", ", queueChanges.Select(c => $"{c.Op}:{c.Title}")));

            Shared.Logger.LogInformation("[Engram] [{ThreadKey}] sweep complete.", threadKey);
        }
        finally
        {
            sweepingThreads.TryRemove(threadKey, out _);
            engramLock.Release();
            SweepCompleted?.Invoke(threadKey);
        }
    }

    private async Task<bool> Classify(IReadOnlyList<ThreadItem> recentItems, string trigger)
    {
        string transcript = BuildTranscript(recentItems);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            Shared.Logger.LogInformation("[Engram] [{Trigger}] no new messages to classify, skipping.", trigger);
            return false;
        }

        object requestBody = new
        {
            model    = "local",
            messages = new[]
            {
                new { role = "system", content = "You classify whether a conversation contains information worth storing as a long-term memory.\n<|think_off|>" },
                new { role = "user",   content =
                    "Does the following conversation contain anything worth storing as a long-term memory — " +
                    "personal facts, relationships, events, or world knowledge about the user or their life?\n\n" +
                    "A purely task-focused exchange (coding, debugging, technical problem-solving, or general Q&A) does NOT qualify.\n\n" +
                    $"CONVERSATION:\n{transcript}\n\n" +
                    "Respond with only 'yes' or 'no'." }
            },
            stream      = false,
            max_tokens  = 5,
            temperature = 0.0
        };

        try
        {
            HttpRequestMessage request = new(HttpMethod.Post, $"{Endpoint}/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            HttpResponseMessage response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(responseJson);
            string answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            bool worthStoring = answer.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
            if (!worthStoring)
                Shared.Logger.LogInformation("[Engram] [{Trigger}] classified as task-only, skipping extraction.", trigger);
            return worthStoring;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogWarning("[Engram] Classification failed ({Error}), proceeding with extraction.", ex.Message);
            return true;
        }
    }

    private static string BuildTranscript(IEnumerable<ThreadItem> items)
    {
        StringBuilder sb = new();
        foreach (ThreadItem item in items)
        {
            switch (item)
            {
                case UserMessage u: sb.AppendLine($"{u.Username}: {u.Content}"); break;
                case AriResponse r: sb.AppendLine($"ARI: {r.ContentText}");      break;
            }
        }
        return sb.ToString();
    }

    private record EngramPlanItem(string Op, string Name, string Summary, string? NewName = null);

    private static List<EngramPlanItem> ParsePlanManifest(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("plan", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return new();

            List<EngramPlanItem> items = new();
            foreach (JsonElement el in arr.EnumerateArray())
            {
                string? op      = el.GetString("op");
                string? name    = el.GetString("name");
                string? summary = el.GetString("summary");
                string? newName = el.GetString("newName");
                if (!string.IsNullOrWhiteSpace(op) && !string.IsNullOrWhiteSpace(name))
                    items.Add(new EngramPlanItem(op, name, summary ?? string.Empty, newName));
            }
            return items;
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError("[Engram] Failed to parse plan manifest: {Error}. Raw: {Raw}", ex.Message, raw);
            return new();
        }
    }

    private static List<string> ParseFetchList(string raw)
    {
        try
        {
            raw = raw.Trim();
            int start = raw.IndexOf('{');
            if (start >= 0) raw = raw[start..];
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("fetch", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch { }
        return new List<string>();
    }

    private static (List<EngramAdd> adds, List<EngramEdit> edits) ParseEngramOutput(string raw)
    {
        raw = Regex.Replace(raw, @"```[a-zA-Z]*\n?", "").Trim('`').Trim();

        int start = raw.IndexOf('{');
        if (start >= 0) raw = raw[start..];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            List<EngramAdd> adds = new();
            if (root.TryGetProperty("add", out JsonElement addArr) && addArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in addArr.EnumerateArray())
                {
                    string? noteName = el.GetString("name");
                    string? content  = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(noteName) && content is not null)
                        adds.Add(new EngramAdd { NoteName = noteName, Content = content, Aliases = ParseAliases(el) });
                }
            }

            List<EngramEdit> edits = new();
            if (root.TryGetProperty("edit", out JsonElement editArr) && editArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement el in editArr.EnumerateArray())
                {
                    string? noteName    = el.GetString("name");
                    string? newNoteName = el.GetString("newName");
                    string? content     = el.GetString("content");
                    if (!string.IsNullOrWhiteSpace(noteName) && content is not null)
                        edits.Add(new EngramEdit { NoteName = noteName, NewNoteName = newNoteName, Content = content, Aliases = ParseAliases(el) });
                }
            }

            return (adds, edits);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError("[Engram] Failed to parse note output: {Error}. Raw: {Raw}", ex.Message, raw);
            return (new(), new());
        }
    }

    private static IReadOnlyList<string> ParseAliases(JsonElement el)
    {
        if (!el.TryGetProperty("aliases", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}

internal class EngramBuffer
{
    private const int DRAIN_QUEUE_LIMIT = 10;

    private readonly Dialogue      dialogue;
    private readonly Engram        engram;
    private readonly Queue<string> queue            = new();
    private readonly HashSet<string> queuedKeys     = new();

    internal EngramBuffer(Dialogue dialogue, Engram engram)
    {
        this.dialogue = dialogue;
        this.engram   = engram;

        dialogue.ThreadBecameInactive += threadKey =>
        {
            if (queuedKeys.Contains(threadKey)) return;
            queue.Enqueue(threadKey);
            queuedKeys.Add(threadKey);

            if (queue.Count >= DRAIN_QUEUE_LIMIT)
                _ = Task.Run(Drain);
            else if (!dialogue.OwnThreads.Any(t => t.State == ThreadState.Active))
                _ = Task.Run(Drain);
        };
    }

    internal void Remove(string threadKey) => queuedKeys.Remove(threadKey);

    internal async Task Drain()
    {
        while (true)
        {
            if (queue.Count == 0) return;
            string threadKey = queue.Dequeue();
            queuedKeys.Remove(threadKey);

            Thread? thread = dialogue.GetThread(threadKey);
            if (thread is null || thread.State == ThreadState.Active || thread.State == ThreadState.Deleted)
                continue;

            await engram.RunEngram(threadKey, "inactivity");
            dialogue.GetThread(threadKey)?.MarkEngramProcessed();

            if (dialogue.OwnThreads.Any(t => t.State == ThreadState.Active))
                return;
        }
    }
}

file static class JsonElementExtensions
{
    internal static string? GetString(this JsonElement el, string prop)
        => el.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

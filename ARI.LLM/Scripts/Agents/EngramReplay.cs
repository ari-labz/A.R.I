using ARI.BrainVault;
using ARI.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Engram's third input: saved chat logs, read again from the start. Each log is swept like a conversation that just ended (extract,
/// save, day-log entry), oldest first, while ARI is idle. Used to rebuild a brain from everything already said, for example after
/// Engram's rules change. A "fresh" replay empties the brain first, and only ever in DevMode, where the brain is a throwaway copy.
/// When every log is read, a rework of the whole brain is queued, so the result is reshaped by the same rules.
/// </summary>
internal partial class Engram
{
    private sealed class ReplayJob
    {
        public List<string> Files  { get; set; } = new();   // chat log paths, oldest first
        public bool         Fresh  { get; set; }
        public List<string> Done   { get; set; } = new();
        public bool         Rework { get; set; } = true;
    }

    private static readonly Regex LogLine = new(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] ([^:\n]{1,60}): ", RegexOptions.Multiline | RegexOptions.Compiled);

    internal static List<string> QueuedReplays() =>
        Directory.Exists(QueueDir) ? Directory.EnumerateFiles(QueueDir, "*.replay.json").Order(StringComparer.Ordinal).ToList() : new();

    /// <summary>Queues a replay of the given chat logs (oldest first by their first message). Returns the job's file name.</summary>
    internal static string QueueReplay(IEnumerable<string> files, bool fresh, bool rework = true)
    {
        Directory.CreateDirectory(QueueDir);
        List<string> ordered = files.Select(f => (f, FirstStamp(f))).Where(x => x.Item2 is not null).OrderBy(x => x.Item2).Select(x => x.f).ToList();
        string file = Path.Combine(QueueDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.replay.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new ReplayJob { Files = ordered, Fresh = fresh && Shared.DevMode, Rework = rework }, JobJson));
        return Path.GetFileName(file);
    }

    private static DateTime? FirstStamp(string path)
    {
        try
        {
            Match m = LogLine.Match(File.ReadAllText(path));
            return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t) ? t : null;
        }
        catch { return null; }
    }

    /// <summary>Works through one replay job, one chat log at a time; pauses (keeping its place) when ARI is busy. True once finished.</summary>
    internal async Task<bool> RunReplay(string file, Func<bool> stillIdle)
    {
        ReplayJob? job;
        try { job = JsonSerializer.Deserialize<ReplayJob>(File.ReadAllText(file), JobJson); }
        catch { DropSnapshot(file); return true; }
        if (job is null) { DropSnapshot(file); return true; }

        if (job.Fresh && job.Done.Count == 0 && Shared.DevMode)
        {
            await engramLock.WaitAsync();
            try
            {
                int gone = Brain.PurgeAllNotes();
                Commit($"Replay: start a fresh brain ({gone} note(s) cleared)");
                Shared.Logger.LogInformation("[Engram] replay: fresh brain — {Gone} note(s) cleared (DevMode copy only).", gone);
            }
            finally { engramLock.Release(); }
            job.Fresh = false;
            Save(file, job);
        }

        // Older logs name the person "User"; they are the same person the newer logs name, so read them under that name.
        string? owner = job.Files.Select(ReadLog).OfType<(string Speaker, DateTime, DateTime, string)>().Select(l => l.Speaker)
            .Where(n => !GenericSpeakers.Contains(n)).GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

        // One sweep per day: every chat of that day read together, so a day's subjects are filed once and its log entry is whole.
        foreach (IGrouping<DateTime, string> day in job.Files.Where(f => !job.Done.Contains(f)).GroupBy(f => FirstStamp(f)!.Value.Date).OrderBy(g => g.Key).ToList())
        {
            if (!stillIdle()) { Save(file, job); return false; }
            if (ServerDownUntil(out TimeSpan wait)) { Save(file, job); await Task.Delay(wait); return false; }
            bool? ok = await SweepDay(day.Key, day.ToList(), owner);
            if (ok is false) { Save(file, job); return false; }   // the model didn't answer: this day again next time
            job.Done.AddRange(day);
            Save(file, job);
            Shared.Logger.LogInformation("[Engram] replay: {Day:yyyy-MM-dd} done ({N} log(s)) — {Done}/{All} logs read.", day.Key, day.Count(), job.Done.Count, job.Files.Count);
        }
        if (job.Rework) QueueRework(null, "");
        Shared.Logger.LogInformation("[Engram] replay finished — {N} chat log(s) read.", job.Files.Count);
        DropSnapshot(file);
        return true;
    }

    private const int REPLAY_SAVE_BATCH = 10;   // entities per save call: a whole day at once overruns one turn and facts get dropped

    /// <summary>Reads one saved chat log: its speaker, first and last message, and its text with times only. Null when the person said
    /// next to nothing (a test, a ping) or the file can't be read.</summary>
    private static (string Speaker, DateTime First, DateTime Last, string Text)? ReadLog(string path)
    {
        string text;
        try { text = File.ReadAllText(path); } catch { return null; }
        int start = text.IndexOf("\n=====", StringComparison.Ordinal);
        if (start >= 0) text = text[(text.IndexOf('\n', start + 1) + 1)..];
        List<Match> lines = LogLine.Matches(text).ToList();
        if (lines.Count == 0) return null;
        List<string> speakers = lines.Select(m => m.Groups[2].Value.Trim()).Where(n => !n.Equals(AssistantName(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (speakers.Count == 0) return null;
        string speaker = speakers.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        int saidWords = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Groups[2].Value.Trim() != speaker) continue;
            int from = lines[i].Index + lines[i].Length, to = i + 1 < lines.Count ? lines[i + 1].Index : text.Length;
            saidWords += text[from..to].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        if (saidWords < 12) return null;
        DateTime Stamp(Match m) => DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string body = Regex.Replace(text, @"^\[\d{4}-\d{2}-\d{2} (\d{2}:\d{2}):\d{2}\] ", "[$1] ", RegexOptions.Multiline).Trim();
        return (speaker, Stamp(lines[0]), Stamp(lines[^1]), body);
    }

    /// <summary>Sweeps one day of saved chats together. Null when there was nothing to read, false when the model didn't answer.</summary>
    private async Task<bool?> SweepDay(DateTime day, List<string> paths, string? owner)
    {
        List<(string Speaker, DateTime First, DateTime Last, string Text)> logs = paths.Select(ReadLog).OfType<(string, DateTime, DateTime, string)>().OrderBy(l => l.Item2).ToList();
        if (owner is not null)
            logs = logs.Select(l => GenericSpeakers.Contains(l.Speaker)
                ? (owner, l.First, l.Last, Regex.Replace(l.Text, $@"^(\[\d{{2}}:\d{{2}}\] ){Regex.Escape(l.Speaker)}: ", $"$1{owner}: ", RegexOptions.Multiline))
                : l).ToList();
        if (logs.Count == 0) return null;
        string speaker = logs.GroupBy(l => l.Speaker).OrderByDescending(g => g.Sum(l => l.Text.Length)).First().Key;
        string transcript = string.Join("\n\n", logs.Select(l => $"--- a conversation at {l.First:HH:mm}{(l.Speaker != speaker ? $" with {l.Speaker}" : "")} ---\n{l.Text}"));
        DateTime first = logs.Min(l => l.First), last = logs.Max(l => l.Last);

        await engramLock.WaitAsync();
        try
        {
            Brain.Index();
            List<ExtractedEntity>? entities = await ExtractAsync(transcript, "");
            NoteServerAnswer(entities is not null);
            if (entities is null) return false;
            string key = $"replay:{day:yyyy-MM-dd}";
            Thread source = ReplayThread(key, transcript);
            for (int i = 0; i < entities.Count; i += REPLAY_SAVE_BATCH)
                await SaveAllAsync(key, entities.Skip(i).Take(REPLAY_SAVE_BATCH).ToList(), speaker, first.ToString("yyyy-MM-dd"), source: source);
            if (entities.Count > 0)
            {
                string logLine = await GenerateConversationSummary(transcript.Length > 60000 ? transcript[^60000..] : transcript, speaker);
                if (!string.IsNullOrWhiteSpace(logLine)) AppendToConversationLog(first, logLine, first, last);
            }
            CleanLinks();
            Shared.Logger.LogInformation("[Engram] replay: {Day:yyyy-MM-dd} — {N} thing(s) to remember from {Logs} chat(s).", day, entities.Count, logs.Count);
            return true;
        }
        finally { engramLock.Release(); }
    }

    private static string AssistantName() => "ARI";
    private static readonly HashSet<string> GenericSpeakers = new(StringComparer.OrdinalIgnoreCase) { "User", "Admin" };   // what older logs call the person

    // The save step checks every fact against the conversation itself; for a replayed log that is the log's own text, given as a
    // throwaway thread of prompts and replies so the existing "source of truth" path reads it the same way as a live chat.
    private static Thread ReplayThread(string key, string transcript)
    {
        Thread t = new(ThreadPipeline.Dialogue, key) { Internal = true };
        foreach (Match m in Regex.Matches(transcript, @"^\[(\d{2}:\d{2})\] ([^:\n]{1,60}): (.*?)(?=^\[\d{2}:\d{2}\] |\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            string who = m.Groups[2].Value.Trim(), said = m.Groups[3].Value.Trim();
            if (who.Equals("ARI", StringComparison.OrdinalIgnoreCase)) t.History.Add(new Response { Content = ContentBlock.Parse(said), State = State.Complete });
            else t.History.Add(new Prompt { Text = said, AuthorName = who });
        }
        return t;
    }
}

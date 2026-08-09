using ARI.Common;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ARI.LLM;

/// <summary>One turn of the conversation that led up to a rated response. Prose only — the same text the
/// model itself sees on a later turn, with UI display markup stripped (see ThreadItem.ContextText).</summary>
public sealed record FeedbackTurn(string Role, string Author, DateTime Timestamp, string Text);

/// <summary>
/// A response the user rated, with why. The whole point is to be trainable later, so everything needed to
/// read the rating back in isolation is snapshotted at vote time — the response text and the turns that
/// led to it — rather than referenced. Threads are deleted after a few days of dormancy and session
/// records are pruned at 30 days; a pointer into either would rot long before the dataset is worth using.
/// </summary>
public sealed record ResponseFeedback
{
    public required string Id { get; init; }

    /// <summary>"up" or "down".</summary>
    public required string Vote { get; set; }

    /// <summary>Why the user liked or disliked it. Optional — the vote is recorded on click, the note
    /// arrives after, if at all.</summary>
    public string Note { get; set; } = "";

    public required DateTime CreatedAt { get; init; }
    public DateTime          UpdatedAt { get; set; }

    public required string ThreadKey   { get; init; }
    public string?         ThreadTitle { get; init; }
    public string          Pipeline    { get; init; } = "";

    /// <summary>Identifies the rated response within its thread, so a re-vote updates rather than duplicates.</summary>
    public required DateTime ResponseTimestamp { get; init; }

    /// <summary>The rated response itself.</summary>
    public required string Response { get; init; }

    /// <summary>The turns before it, oldest first — the prompt it answered plus the couple of exchanges
    /// before that, so the rating can be read without the surrounding thread.</summary>
    public IReadOnlyList<FeedbackTurn> Context { get; init; } = Array.Empty<FeedbackTurn>();
}

/// <summary>
/// Append-only JSONL of every rated response, at Feedback/responses.jsonl. One line per record so a vote
/// costs an append and the file survives a partial write — a corrupt tail loses one rating, not the set.
/// Edits and deletes rewrite the file, which is fine at the scale this grows (a handful of votes a day).
/// </summary>
public static class FeedbackStore
{
    private static readonly object Lock = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy    = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition  = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented           = false,
    };

    private static string FilePath => Path.Combine(Paths.Feedback, "responses.jsonl");

    /// <summary>Every rating, newest first. Unparseable lines are skipped rather than throwing — one bad
    /// line must never take the whole dataset offline.</summary>
    public static List<ResponseFeedback> Load()
    {
        lock (Lock) { return LoadUnlocked(); }
    }

    private static List<ResponseFeedback> LoadUnlocked()
    {
        List<ResponseFeedback> list = new();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ResponseFeedback>(line, Json) is { } fb) list.Add(fb);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static void SaveUnlocked(List<ResponseFeedback> list)
    {
        Directory.CreateDirectory(Paths.Feedback);
        File.WriteAllLines(FilePath, list.Select(fb => JsonSerializer.Serialize(fb, Json)));
    }

    /// <summary>Records a rating. Re-rating the same response replaces the existing record rather than
    /// adding a second one, so the dataset never holds two verdicts on one response.</summary>
    public static ResponseFeedback Save(ResponseFeedback feedback)
    {
        lock (Lock)
        {
            List<ResponseFeedback> all = LoadUnlocked();
            int existing = all.FindIndex(f => f.ThreadKey == feedback.ThreadKey
                                           && f.ResponseTimestamp == feedback.ResponseTimestamp);

            if (existing < 0)
            {
                Directory.CreateDirectory(Paths.Feedback);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(feedback, Json) + Environment.NewLine);
                return feedback;
            }

            // Keep the original record's id and creation time — the user is changing their mind about a
            // response they already rated, not rating a new one.
            ResponseFeedback merged = feedback with
            {
                Id        = all[existing].Id,
                CreatedAt = all[existing].CreatedAt,
                UpdatedAt = DateTime.Now,
                Note      = string.IsNullOrWhiteSpace(feedback.Note) ? all[existing].Note : feedback.Note,
            };
            all[existing] = merged;
            SaveUnlocked(all);
            return merged;
        }
    }

    /// <summary>Changes the vote and/or note on an existing rating. Null leaves that field alone.</summary>
    public static ResponseFeedback? Update(string id, string? vote, string? note)
    {
        lock (Lock)
        {
            List<ResponseFeedback> all = LoadUnlocked();
            int i = all.FindIndex(f => f.Id == id);
            if (i < 0) return null;

            if (vote is not null) all[i].Vote = vote;
            if (note is not null) all[i].Note = note;
            all[i].UpdatedAt = DateTime.Now;

            SaveUnlocked(all);
            return all[i];
        }
    }

    public static bool Remove(string id)
    {
        lock (Lock)
        {
            List<ResponseFeedback> all = LoadUnlocked();
            if (all.RemoveAll(f => f.Id == id) == 0) return false;
            SaveUnlocked(all);
            return true;
        }
    }

    /// <summary>Ratings in one thread — lets the chat UI show which responses are already rated.</summary>
    public static List<ResponseFeedback> ForThread(string threadKey) =>
        Load().Where(f => f.ThreadKey == threadKey).ToList();
}

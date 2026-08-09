using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace ARI.API.Controllers;

/// <summary>
/// Response ratings — the thumbs up/down beside each of Ari's replies, and the Response Feedback tab in
/// the control panel that browses them. Snapshotting happens here rather than client-side because only
/// the server holds the clean, markup-free text the model actually produced.
/// </summary>
[Route("feedback")]
[ApiController]
public class FeedbackController : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>How many user prompts of lead-up to snapshot: the one this response answered, plus the two
    /// exchanges before it — enough to tell later why a reply read well or badly.</summary>
    private const int CONTEXT_PROMPTS = 3;

    /// <summary>Hard ceiling on snapshotted turns, so a long tool-heavy run can't bloat one record.</summary>
    private const int CONTEXT_MAX_ITEMS = 12;

    // ── Chat UI ───────────────────────────────────────────────────────────────────

    /// <summary>Records (or replaces) a rating on one response. The vote is what matters and is saved on
    /// click; the note is optional and usually arrives a moment later via PATCH.</summary>
    [HttpPost]
    public IActionResult Rate([FromBody] RateRequest req)
    {
        if (Llm is null) return StatusCode(503, "ARI is not ready yet.");

        string vote = (req.Vote ?? "").Trim().ToLowerInvariant();
        if (vote != "up" && vote != "down") return BadRequest("vote must be 'up' or 'down'.");

        if (string.IsNullOrWhiteSpace(req.ThreadKey) ||
            !Llm.Threads.TryGetValue(req.ThreadKey, out ARI.LLM.Thread? thread))
            return NotFound("No such thread.");

        // The same filter GET /threads/{key} applies, so the client's index lines up with this list.
        List<ThreadItem> visible = thread.History
            .Where(i => i.IsVisible && i is not Response { State: State.Cancelled })
            .ToList();

        int index = ResolveIndex(visible, req.MessageIndex, req.Timestamp);
        if (index < 0) return NotFound("That response is no longer in the thread.");
        if (visible[index] is not Response response) return BadRequest("That item is not a response.");

        // ContextText is prose only — no tool-card markup. It is what the model produced and what any
        // future finetune would be trained against.
        string text = response.ContextText ?? response.ContentText;
        if (string.IsNullOrWhiteSpace(text)) return BadRequest("That response has no text to rate.");

        ResponseFeedback saved = FeedbackStore.Save(new ResponseFeedback
        {
            Id                = Guid.NewGuid().ToString("N"),
            Vote              = vote,
            Note              = (req.Note ?? "").Trim(),
            CreatedAt         = DateTime.Now,
            UpdatedAt         = DateTime.Now,
            ThreadKey         = thread.Key,
            ThreadTitle       = thread.Title,
            Pipeline          = thread.Pipeline.ToString().ToLowerInvariant(),
            ResponseTimestamp = response.Timestamp,
            Response          = text,
            Context           = BuildContext(visible, index),
        });

        return Ok(saved);
    }

    /// <summary>Ratings already recorded in a thread, so the chat UI can light up the right thumb on load.</summary>
    [HttpGet("thread/{threadKey}")]
    public IActionResult ForThread(string threadKey) =>
        Ok(FeedbackStore.ForThread(threadKey).Select(f => new
        {
            id                = f.Id,
            vote              = f.Vote,
            note              = f.Note,
            responseTimestamp = f.ResponseTimestamp,
        }));

    // ── Control panel ─────────────────────────────────────────────────────────────

    /// <summary>The whole dataset, newest first. Optional <c>vote</c> filter ("up"/"down").</summary>
    [HttpGet]
    public IActionResult List([FromQuery] string? vote)
    {
        List<ResponseFeedback> all = FeedbackStore.Load();

        // Counts are of the whole set, not the filtered view — the tab shows them as totals.
        int up   = all.Count(f => f.Vote == "up");
        int down = all.Count(f => f.Vote == "down");

        if (!string.IsNullOrWhiteSpace(vote))
            all = all.Where(f => f.Vote == vote.ToLowerInvariant()).ToList();

        return Ok(new
        {
            up,
            down,
            items = all.OrderByDescending(f => f.CreatedAt).ToList(),
        });
    }

    [HttpPatch("{id}")]
    public IActionResult Edit(string id, [FromBody] EditRequest req)
    {
        string? vote = req.Vote?.Trim().ToLowerInvariant();
        if (vote is not null && vote != "up" && vote != "down") return BadRequest("vote must be 'up' or 'down'.");

        ResponseFeedback? updated = FeedbackStore.Update(id, vote, req.Note);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) =>
        FeedbackStore.Remove(id) ? Ok(new { ok = true }) : NotFound();

    /// <summary>Every rating as JSONL — the raw dataset, ready to be reshaped into training pairs.</summary>
    [HttpGet("export")]
    public IActionResult Export()
    {
        string path = Path.Combine(Paths.Feedback, "responses.jsonl");
        byte[] bytes = System.IO.File.Exists(path)
            ? System.IO.File.ReadAllBytes(path)
            : Array.Empty<byte>();
        return File(bytes, "application/jsonl", $"ari-feedback-{DateTime.Now:yyyyMMdd-HHmm}.jsonl");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the rated response. The timestamp is the real identity — the index is only a hint, and it
    /// drifts whenever the thread grew between render and click. Falls back to the index when no
    /// timestamp was sent.
    /// </summary>
    private static int ResolveIndex(List<ThreadItem> visible, int messageIndex, string? timestamp)
    {
        if (!string.IsNullOrWhiteSpace(timestamp) &&
            DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ts))
        {
            int match = visible.FindIndex(i => i is Response && Math.Abs((i.Timestamp - ts).TotalMilliseconds) < 1);
            if (match >= 0) return match;
        }

        return messageIndex >= 0 && messageIndex < visible.Count ? messageIndex : -1;
    }

    /// <summary>Walks back from the rated response collecting the lead-up, stopping after
    /// <see cref="CONTEXT_PROMPTS"/> user prompts. Returns oldest first.</summary>
    private static List<FeedbackTurn> BuildContext(List<ThreadItem> visible, int index)
    {
        List<FeedbackTurn> turns = new();
        int prompts = 0;

        for (int i = index - 1; i >= 0 && turns.Count < CONTEXT_MAX_ITEMS; i--)
        {
            ThreadItem item = visible[i];
            string text = (item.ContextText ?? item.Message ?? "").Trim();
            if (text.Length == 0) continue;

            turns.Add(new FeedbackTurn(
                item is Prompt ? "user" : "ari",
                string.IsNullOrEmpty(item.AuthorName) ? (item is Prompt ? "User" : "ARI") : item.AuthorName,
                item.Timestamp,
                text));

            if (item is Prompt && ++prompts >= CONTEXT_PROMPTS) break;
        }

        turns.Reverse();
        return turns;
    }
}

public record RateRequest(string ThreadKey, int MessageIndex, string? Timestamp, string? Vote, string? Note);
public record EditRequest(string? Vote, string? Note);

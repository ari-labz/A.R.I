using System.Text.Json;
using System.Text.Json.Nodes;
using ARI.LLM;

namespace ARI.API;

/// <summary>
/// The structured way to read a reply as it is written (<c>POST /threads/{id}/messages?format=v2</c>): typed JSON events
/// that carry only what is new, instead of the whole reply so far with markers in it. It is built from the same stream the
/// older format uses, so the two always agree, and the finished message at the end is exactly what
/// <c>GET /threads/{id}/messages</c> returns for it.
///
///   message.start   { threadId }
///   block.start     { index, block }          a new piece of the reply: a run of text, or a tool card
///   text.delta      { index, text }           more words for the text block at that index (append them)
///   block.update    { index, block }          a block changed in some other way (a tool card finished): replace it
///   message.split   { kind, user, text }      the user interjected and the reply continues in a new message
///   message.done    { message }               the reply is finished; message is the complete saved reply
///   message.cancelled {}                      the reply was stopped before it finished
///   error           { message }               something went wrong
/// </summary>
public sealed class StreamEvents(JsonSerializerOptions json)
{
    private int nextId;
    private List<JsonNode> previous = [];

    /// <summary>One event, in server-sent-events form.</summary>
    public string Format(string name, object? data)
        => $"id: {++nextId}\nevent: {name}\ndata: {JsonSerializer.Serialize(data ?? new { }, json)}\n\n";

    /// <summary>The events for one update of the reply-so-far (what the older format sends as a whole).</summary>
    public IEnumerable<string> FromAccumulated(string accumulated)
    {
        if (accumulated.StartsWith("[SPLIT]", StringComparison.Ordinal))
        {
            string[] parts = accumulated["[SPLIT]".Length..].Split('\u0001');
            previous = [];   // what follows is a new message
            yield return Format("message.split", new { kind = parts.ElementAtOrDefault(0), user = parts.ElementAtOrDefault(1), text = parts.ElementAtOrDefault(2) });
            yield break;
        }

        List<JsonNode> now = ContentBlock.Parse(accumulated)
            .Where(b => b.IsVisible)
            .Select(b => JsonSerializer.SerializeToNode<ContentBlock>(b, json)!)
            .ToList();

        for (int i = 0; i < now.Count; i++)
        {
            if (i >= previous.Count) { yield return Format("block.start", new { index = i, block = now[i] }); continue; }

            string before = previous[i].ToJsonString(), after = now[i].ToJsonString();
            if (before == after) continue;

            bool bothText = Type(previous[i]) == "text" && Type(now[i]) == "text";
            string oldText = bothText ? previous[i]["text"]?.GetValue<string>() ?? "" : "";
            string newText = bothText ? now[i]["text"]?.GetValue<string>() ?? "" : "";
            if (bothText && newText.StartsWith(oldText, StringComparison.Ordinal) && newText.Length > oldText.Length)
                yield return Format("text.delta", new { index = i, text = newText[oldText.Length..] });
            else
                yield return Format("block.update", new { index = i, block = now[i] });
        }
        previous = now;
    }

    private static string? Type(JsonNode n) => n["type"]?.GetValue<string>();
}

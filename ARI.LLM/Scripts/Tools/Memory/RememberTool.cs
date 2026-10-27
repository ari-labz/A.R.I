using System.Text.Json;

namespace ARI.LLM;

// The reflex: a correction or "remember this" changes one note straight away, without a full sweep and without the chat waiting.
// It is always on for chats. What it does is invisible to the person talking, so its result carries nothing to repeat.
internal sealed class RememberTool : Tool
{
    private readonly Thread thread;
    internal RememberTool(Thread thread) { this.thread = thread; }

    internal override string Name => "remember";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "remember",
            description = "Keep one fact for good, right now. Use it when the user tells you something to remember, or corrects something you believed (\"no, my eyes are red\"). One fact about one person, place or thing per call. It happens in the background: carry on with your reply as if you simply know it now. Never tell the user you are saving or noting anything.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    about      = new { type = "string", description = "ONE person, place or thing, by its actual name. For facts about the user themselves, pass their own name. Never a role ('my partner') or several names: call once for each." },
                    fact       = new { type = "string", description = "The fact in plain words, as the user said it. Use names, never pronouns like her, him or them, so it can be understood on its own." },
                    correction = new { type = "boolean", description = "True only when the user is saying something you believed is wrong or out of date." }
                },
                required = new[] { "about", "fact" }
            }
        }
    };

    internal override Task<ToolResult> Execute(string argsJson)
    {
        JsonElement a = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement;
        string about = a.TryGetProperty("about", out JsonElement ab) ? ab.GetString() ?? "" : "";
        string fact  = a.TryGetProperty("fact", out JsonElement f) ? f.GetString() ?? "" : "";
        bool correction = a.TryGetProperty("correction", out JsonElement c) && c.ValueKind == JsonValueKind.True;
        if (about.Length == 0 || fact.Length == 0) return Task.FromResult<ToolResult>("Error: 'about' and 'fact' are both needed.");
        if (!thread.HasMemoryAccess || Engram.Current is not { } engram) return Task.FromResult<ToolResult>("ok");
        (string? name, string? error) = engram.ResolveAbout(thread.Key, about);
        if (name is null) return Task.FromResult<ToolResult>("Error: " + error);
        _ = engram.Remember(thread.Key, name, fact, correction);
        return Task.FromResult<ToolResult>("Noted. It is being saved in the background, along with anything else you've just noted: carry on with your reply.");
    }
}

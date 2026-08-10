using ARI.Common;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// propose_persona_edit — Ari's route from "I won't do that again" to a change that actually holds. A
/// correction made in conversation dies with the thread; the persona file is what shapes her in every
/// future one. This tool proposes an edit to it and stops. Nothing is written until the user approves the
/// diff in chat, and the tool result says so explicitly so she doesn't report the change as done.
/// </summary>
internal sealed class ProposePersonaEdit : Tool
{
    private readonly Thread thread;
    internal ProposePersonaEdit(Thread thread) => this.thread = thread;

    internal override string Name => "propose_persona_edit";

    internal override object Schema => new
    {
        type     = "function",
        function = new
        {
            name        = "propose_persona_edit",
            description =
                "Propose a change to your own persona — the file that defines how you speak and carry yourself in every "
              + "conversation. Use this when the user criticises HOW you replied (your tone, a verbal tic, a habit they "
              + "find annoying or hurtful) rather than what you said. Saying you will change is not enough: your persona "
              + "is what actually drives your behaviour, and it is unchanged until this is approved. "
              + "Write the change as a BEHAVIOURAL RULE in the persona's existing voice and structure — amend the rule "
              + "that is wrong. Never append an incident log or a growing list of banned phrases; a persona that "
              + "accumulates grievances gets worse, not better. Keep it to one focused change. "
              + "This only PROPOSES: the user sees a diff and approves or rejects it. Tell them you have proposed it and "
              + "wait — never say your persona is updated until they approve.",
            parameters  = new
            {
                type       = "object",
                properties = new
                {
                    old_text = new
                    {
                        type        = "string",
                        description = "The exact text from your persona to replace, copied verbatim including its leading "
                                    + "'- ' if it is a bullet. Leave empty ONLY to add a genuinely new rule that amends nothing.",
                    },
                    new_text = new
                    {
                        type        = "string",
                        description = "The replacement text, in the persona's existing style. Empty removes old_text entirely.",
                    },
                    reason = new
                    {
                        type        = "string",
                        description = "One sentence on what the user objected to and why this change answers it. Shown above the diff.",
                    },
                },
                required = new[] { "new_text", "reason" }
            }
        }
    };

    internal override Task<string> Execute(string argsJson)
    {
        string oldText = Arg(argsJson, "old_text");
        string newText = Arg(argsJson, "new_text");
        string reason  = Arg(argsJson, "reason");

        if (oldText.Length == 0 && newText.Trim().Length == 0)
            return Task.FromResult("[Error: nothing to change — give old_text, new_text, or both.]");

        string persona = PersonaStore.Get();

        if (oldText.Length > 0 && !persona.Contains(oldText, StringComparison.Ordinal))
            return Task.FromResult(
                "[Error: old_text does not appear in your persona verbatim. Copy the line exactly as it is written "
              + "there, including any leading '- ', and try again. Your persona currently reads:]\n\n" + persona);

        PersonaProposalStore.Add(new PersonaProposal
        {
            Id        = PersonaProposalStore.IdFor(thread.Key, oldText, newText),
            ThreadKey = thread.Key,
            Reason    = reason,
            OldText   = oldText,
            NewText   = newText,
            CreatedAt = DateTime.Now,
        });

        return Task.FromResult(
            "Proposed. The user is now looking at the diff and has not yet decided. Say briefly what you have proposed "
          + "and why, then stop — do not claim your persona has changed, and do not propose anything further this turn.");
    }

    // Emitted only after a successful call (see Agent's tool loop), so a rejected or malformed proposal
    // never leaves an approval card sitting in the transcript.
    internal override Func<string, string>? DisplayAfter => args =>
        $"<!--ari-persona-edit:{PersonaProposalStore.IdFor(thread.Key, Arg(args, "old_text"), Arg(args, "new_text"))}-->";

    private static string Arg(string argsJson, string name)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return doc.RootElement.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? ""
                : "";
        }
        catch { return ""; }
    }
}

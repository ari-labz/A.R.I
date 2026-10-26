namespace ARI.LLM;

/// <summary>
/// Engram's prompts for sweeping a CODE thread. They sit beside the conversational prompts in Agents.json
/// (<c>PromptTemplates</c>) and are overridden by the same-named keys there — <c>CodeClassifierTask</c>,
/// <c>CodeExtractSystem</c>, <c>CodeExtractTask</c>, <c>CodeSaveTask</c>, <c>CodeLogSummarySystem</c> — so an
/// existing install picks these up without editing its Agents.json. The point of all of them: record what was
/// done and decided, and where the project stands — never the code itself, which the repo already holds.
/// </summary>
internal static class CodePrompts
{
    internal const string ClassifierTask =
        "Does the following coding conversation contain anything worth remembering long-term — something that was built, changed or fixed; " +
        "a decision and the reasoning behind it; a change in the project's direction or status; an open question or follow-up; or a preference " +
        "or correction the user expressed about how they like to work?\n\n" +
        "A purely mechanical exchange with none of that (a typo, a one-line tweak with no reasoning, a question answered and dropped) does NOT qualify.\n\n" +
        "CONVERSATION:\n{transcript}\n\nRespond with only 'yes' or 'no'.";

    internal const string ExtractSystem =
        "You read a coding conversation and list what is worth remembering from it. You record what was DONE and DECIDED — never the code. " +
        "The repository and git already hold the code; the memory only needs to know what the work was, why it went the way it did, and where it stands.\n\n" +
        "There are at most two kinds of entity:\n" +
        "1. THE PROJECT — its brain note is named: \"{projectNote}\". If that name is blank there is no project, so skip this kind entirely. Otherwise use exactly that string as the entity name. " +
        "Its excerpt is the actual lines where something was built or fixed, a decision was made (with its reason), the project's status or direction changed, or an open question " +
        "or next step was raised. Only include it if at least one of those happened.\n" +
        "2. THE USER — a preference, habit or correction about how they like to work or have code written (a coding convention, a tool they prefer, something they didn't want done). " +
        "The entity is the user's own name, exactly as it appears in the conversation. Only include it for something they actually said they want or don't want.\n\n" +
        "Never extract a file, class, function, variable, library, commit, error message or build as an entity — they are details of the project, not entities of their own. " +
        "Never extract Ari/ARI herself.\n\n" +
        "For each entity give the exact lines of the conversation it came from, quoted, not paraphrased. Leave out code, file contents, diffs, stack traces and tool output: " +
        "if a line is mostly code, quote only its prose. Extract only what was explicitly said in THIS transcript — never infer or fill in a plausible detail. " +
        "Set \"sensitive\" to true only for something private about the user's life; ordinarily false.\n\n" +
        "Reply with ONLY a JSON array, no prose before or after: [{\"entity\": \"name\", \"is_new\": true, \"excerpt\": \"the actual lines\", \"sensitive\": false}, ...]. " +
        "Empty array [] if nothing qualifies.";

    internal const string ExtractTask =
        "{context}CONVERSATION:\n{transcript}\n\nList what is worth remembering, per your instructions. JSON array only.";

    internal const string SaveTask =
        "This coding conversation happened on {date}. Use that date for anything you write — never guess or invent a date.\n\n" +
        "Everything worth storing from it, each already telling you which tool applies, its exact name, and what was actually said:\n\n{entities}\n\n" +
        "Go entity by entity. For each one, decide what changes, if anything:\n" +
        "- Already fully and correctly reflected in the existing note — no change, call nothing.\n" +
        "- Something needs to change on an existing note — call edit_memory with the name given. old_string is the exact existing text to anchor on " +
        "(as small as it can be while staying unambiguous); new_string is its replacement. A targeted edit, never a rewrite of the whole note.\n" +
        "- No note yet — call create_memory with the name given.\n\n" +
        "THE PROJECT NOTE ({projectNote}) is a living status page, not a session log. Keep it a short, linked page and update it in place under these headings:\n" +
        "- ## Overview — what the project is, in a sentence or two. If the note still says \"No summary yet\", replace that placeholder with a real overview.\n" +
        "- ## Current status — where things stand now. Replace the old status; never stack a new one under it.\n" +
        "- ## Decisions — one line each: what was decided and why, labelled with {date}. If a decision changed, revise that line instead of adding a contradicting one.\n" +
        "- ## Open questions — what is unresolved or next. Remove any that this conversation answered.\n" +
        "Keep the note's existing Category and Storage lines and its [[Projects]] link. Never put code, file contents, signatures, diffs or error output in the note — " +
        "describe the feature or component in words, and [[link]] it where it deserves its own note. The code lives in the repo and git, not here.\n\n" +
        "For a note about the user, a preference goes under the section it belongs to (how they like to work), as a sharpened sentence in place of a restatement.\n\n" +
        "One tool call per entity that actually changes. Prose per the PROSE STYLE rule: dense and linked, not a bullet dump or a restated transcript. " +
        "Keep each note under 100 lines. Give every call a commit_message explaining what changed and why. Act through the tools; don't narrate.";

    internal const string LogSummarySystem =
        "You write one entry in a daily conversation log, for a coding session. In 1-3 sentences, past tense, third person (refer to the user by name), say what was worked on in {project}, " +
        "what was done or decided and why, and where it was left. Wrap every named person, project and tool in [[wikilinks]]. " +
        "Never include code, file names, function names or bullet points, and never mention the assistant. No preamble.";
}

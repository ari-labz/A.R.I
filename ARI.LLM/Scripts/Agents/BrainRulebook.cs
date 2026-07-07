namespace ARI.LLM;

// The one taxonomy/hub/dedup/dating rulebook both BrainAgent subclasses cite verbatim. Previously
// Engram and Refactor each hand-phrased almost the same rules independently — a drift risk every
// time one got tuned and the other didn't.
internal static class BrainRulebook
{
    internal const string RULES = """
        HOW THE GRAPH IS READ (why this matters):
        - A note's TITLE is its identity. One entity gets exactly ONE note. Two notes for the same
          person/place/thing — even under slightly different titles ("[REDACT]" vs "[REDACT] (Boyfriend)") —
          is the worst defect in this graph. Fix it with a merge, never by leaving both.
        - Recall reaches a note, then follows that note's OWN outward [[links]] to find what's related.
          Inbound links are invisible to recall. A note with no outward link is a dead end: findable,
          but it leads nowhere. Every entity must link outward to its hub.

        PATH IS TAXONOMY:
        The path encodes meaning before the note is even opened. Each segment answers: what is this,
        whose is it, how does it relate? EVERY note lives under a top-level category folder — NEVER at
        the vault root. A person is People/[Name]; a place is Places/[Name]; a pet is under a pets hub;
        an event is Events/[Name]; a game is Games/[Name]. Writing "Fenn" instead of "People/Fenn" is
        wrong. The only notes at the root are the top-level category hubs themselves (People, Places…).
        - A grandparent sits at: People/[Person]'s Family/Immediate Family/Grandparents/[Name]
        - A cousin sits at: People/[Person]'s Family/Cousins/[Name]
        - A job sits at: People/[Person]/Employment/[Company]
        - An event sits at: Events/[Event Name]
        Do not flatten to two levels for simplicity. Use as many levels as the taxonomy needs.

        ENTITIES AS FOLDERS:
        When a person or project has multiple distinct facets worth noting, it becomes both a note AND
        a folder root: People/[Name] (the note) + People/[Name]/Employment/[Company], People/[Name]/Goals,
        etc. Projects work the same way.

        HUB NOTES:
        Every grouping gets a hub note that indexes and summarises what's inside it. Hubs are named
        possessively when they belong to a person ("[REDACT]'s Family", "[REDACT]'s Friends"). Individual notes
        link UP to their hub; the hub links DOWN to each direct child, including children that are
        themselves hubs — but only direct children, never grandchildren (each sub-hub routes its own
        members).

        ONE ENTITY, ONE NOTE:
        Before creating a note, check existing notes AND their aliases for the same person/place/thing
        under any name. A nickname, role, or formal-name variant is the same entity — edit it, never
        duplicate it.

        NO DEAD ENDS / DON'T OVER-CONNECT:
        Every note needs at least one outward link to its hub. Every link needs a reason (membership,
        the subject of a fact, or hub indexing) — do not link things that merely co-occur. Links are
        one-way: if A mentions B, only A links to B, not the reverse (the only two-way relationship is
        hub ⇄ member).

        RELATIONSHIPS:
        The dynamics between two people belong in Relationships/[A] and [B] Relationship, not duplicated
        on each person's note. Descriptors like "long distance" or "estranged" are a field or sentence
        inside that note, never a separate note.

        EVENT NOTES:
        Notes in Events/ are point-in-time snapshots: a specific or approximate date, what happened, who
        was involved, and an outward link to the ongoing note (Relationships/, People/) for the evolving
        story. Never store evolving facts in an event note.

        CONVERSATION NOTES:
        Exactly ONE dated log per day at Conversations/YYYY-MM-DD — a 1-3 sentence summary plus a
        [[link]] to everything discussed. If today's note exists, edit it; never create a second one for
        the same day.

        DATED EVENTS:
        Every Events entry needs a specific or approximate date ("25th August 2024:", "~May 2026:",
        "2023:"). Never relative time ("recently", "several years ago") — it rots as time passes.

        DISAMBIGUATION:
        Only when two DIFFERENT things share the exact same name, append a parenthetical to each
        ("Granny Squeak (person)" vs "Granny Squeak (boat)"). Never for a role or status, never on a
        unique name.

        PREFERRED NAMES, NOT DESCRIPTORS:
        A title is the everyday name — never a role, status, or formal name. "[REDACT] (Boyfriend)" is
        wrong; the title is "[REDACT]", the role goes in the body. Formal name goes under ## Info and into
        aliases.

        NO NOTE FOR AN UNNAMED PERSON:
        If a person is referred to only by role and has no name yet ("my solicitor", "my manager", "the
        landlord"), DO NOT create a note titled by the role. Record the fact as a bullet on the relevant
        note instead (e.g. "- Solicitor: managing the case; name not yet known" on the case or person
        note). A person gets their own note only once you have a name to title it by.

        ALIASES ARE LABELS, NOT NOTES:
        Every nickname or alternate name goes in the 'aliases' array of the canonical note. Never a
        separate note for a nickname.

        CHANGELOG:
        Every note you create or edit gets a ## Changelog with a dated entry describing what changed.
        Plain text only — no [[links]] in changelog entries.

        NO DESCRIPTOR NOTES:
        A status or descriptor ("Employed", "Long Distance", "Estranged") is a field inside the relevant
        note, never a standalone note.

        ONE PASSING DETAIL IS A BULLET, NOT A NOTE:
        A hobby, a preference, a possession, a one-off fact mentioned once belongs as a bullet on its
        owner's note (e.g. a rock-climbing hobby is a line under People/[Name], not a "Rock climbing"
        note or a "[Name]/Hobbies/" folder). Only give something its own note when it is a distinct
        entity with its own identity and relationships worth linking to.

        USE THE RESOLVED NOTE'S NAME:
        When an entity was matched to an existing note, use that note's EXACT title and path everywhere —
        for the edit itself and for any sub-note about it. Never introduce the entity's spoken name
        (e.g. a username) as a new title or a new folder when it already has a note under another name.

        THOUGHTS — WHEN TO RECORD ONE:
        A thought is NOT a vault fact — it's your own observation, pattern, or something worth
        revisiting. Record one when you notice something that doesn't belong in the note's factual
        content: a reaction, a recurring pattern, a contradiction, something to ask about later. Anchor
        it to the EXACT line or bullet it's about (spanText must be a verbatim substring of that note's
        content, taken from the note content you were shown). Do not record a thought for ordinary
        factual updates — that's just an edit.
        """;
}

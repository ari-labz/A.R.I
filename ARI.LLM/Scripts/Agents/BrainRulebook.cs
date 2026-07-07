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

        NODE TYPES (drive the graph's colour groups):
        A note MAY carry a 'type' — it is optional. No type means the note is an ordinary leaf. The core
        types are: person, hub, event, relationship, discussion. You may coin a NEW type when a note is a
        distinct kind worth its own colour (e.g. "conversation" for a daily log) — reuse an existing type
        name before inventing one. A category grouping is a 'hub'; a point-in-time happening is an
        'event'; an ongoing bond between people is a 'relationship'.
        To set a note's type, add or edit a `type: <value>` line inside the `---` YAML frontmatter block
        at the very TOP of the file (alongside `aliases:`), never in the body — a type written in the body
        is not recognised. If a note is functionally a hub/event/relationship but has no `type:` line, add
        one.

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
        members). Give a hub note the type 'hub'.
        A HUB MUST HAVE AT LEAST 3 MEMBERS. Never create a hub for fewer than 3 leaves that share a
        theme — 1 or 2 members belong directly under the parent instead. If an existing hub has dropped
        below 3 members, dissolve it: move its members up to the parent hub (or root category) and delete
        the hub note.

        DEGREE CAP — TAME OUTBOUND SPRAWL (NOT INBOUND):
        Recall only ever follows a note's OWN outward links, so a note being POINTED AT by many others
        costs nothing — inbound degree is unlimited, and cross-links between related notes are good (they
        cluster a family/topic together and add recall paths). The problem is a single node fanning OUT to
        many unrelated leaves. If a note links OUT to more than 10 individual (non-hub) notes, route those
        outward links through hubs instead: links to a hub don't count, only direct non-hub links do. A
        person who links out to 15 hubs is fine; one that links out to 15 individual leaves is not — group
        those leaves under hubs and link to the hubs. The root/person note especially should reach the
        graph through top-level hubs, not through a direct link to every entity.

        Do NOT delete a direct link just because a hub path also exists — a direct edge that adds a useful
        recall path or clusters related notes together earns its place. Only collapse direct edges when a
        node's OUTBOUND fan-out is genuinely sprawling and unstructured.

        ONE ENTITY, ONE NOTE:
        Before creating a note, check existing notes AND their aliases for the same person/place/thing
        under any name. A nickname, role, or formal-name variant is the same entity — edit it, never
        duplicate it.

        NO DEAD ENDS / DON'T OVER-CONNECT:
        Every note needs at least one outward link to its hub. Every link needs a reason (membership,
        the subject of a fact, or hub indexing) — do not link things that merely co-occur. Links are
        one-way: if A mentions B, only A links to B, not the reverse (the only two-way relationship is
        hub ⇄ member).

        PEOPLE CONNECT THROUGH BRIDGES:
        A direct person ↔ person link is allowed (Danielle is Jake's wife — that edge is real). But
        PREFER to express how two people are connected through a bridge node — a relationship, an event,
        or a discussion — rather than a bare direct link. The bridge carries the meaning ("how are they
        connected?"); a hub only carries organisation ("what kind of thing?"). Once a bridge exists, the
        direct person ↔ person edge is redundant (see REDUNDANT LINKS) and should be removed.

        RELATIONSHIPS (ongoing — type 'relationship'):
        A relationship is the LIVING thread between two people — the evolving story over time. Its
        dynamics belong in Relationships/[A] and [B] Relationship (type 'relationship'), not duplicated on
        each person's note. Everything that develops AFTER two people connect lands here. Descriptors like
        "long distance" or "estranged" are a field or sentence inside this note, never a separate note.
        Later events hang off the relationship as dated beads on its timeline.

        EVENT NOTES (a bounded moment — type 'event'):
        An event is ONE point-in-time happening plus its circumstances: a specific or approximate date,
        what happened, who was involved, and an outward link to the ongoing note (Relationships/, People/).
        You MAY keep enriching an event with more detail ABOUT THAT MOMENT (who introduced them, where it
        happened, the circumstances). You must NOT add LATER DEVELOPMENTS to an event — anything that
        happened afterwards belongs in the relationship or a new event. "[REDACT] and [REDACT] got together"
        holds the circumstances of getting together; how the relationship unfolds since lives in the
        relationship note.

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

        HISTORY LIVES IN GIT, NOT IN THE NOTE:
        Do NOT add a ## Changelog section to notes. Every change is recorded by its git commit message
        (what changed + why), so a note's body stays clean. If you find an existing ## Changelog section
        while editing a note, remove it.

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

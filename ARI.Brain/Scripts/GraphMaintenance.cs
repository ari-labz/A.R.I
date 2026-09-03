namespace ARI.BrainVault;

// Structural upkeep for the graph itself — the operations Refactor calls to fix defects that
// shouldn't accumulate if Engram's placement is working, not the routine per-conversation writes
// (those are Brain.AddNote/EditNote). Split out from Brain because this is a genuinely separate
// concept: "keep the graph's shape correct" versus "read and write a note."
public static class GraphMaintenance
{
    // A folder full of notes needs a hub note beside it (Pets/ → Pets.md) to index them. Engram places
    // members correctly but doesn't always create the hub note; this makes it deterministic.
    public static int EnsureHubNotes()
    {
        int created = 0;
        foreach (string dir in Directory.EnumerateDirectories(Brain.VaultRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Brain.VaultRoot, dir).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
            if (!Directory.EnumerateFiles(dir, "*.md").Any()) continue;   // no direct child notes → not a hub
            if (File.Exists(dir + ".md")) continue;                        // hub note already exists

            string name = Path.GetFileName(dir);
            Note.Write($"{relative}.md", $"# {name}\n\nHub for {name}.\n", Array.Empty<string>(), null, type: "hub");
            created++;
        }
        if (created > 0) Brain.Index();
        return created;
    }

    // Merges a drifted title variant ("Alex — User", "Jordan - partner") back into its base note when
    // the base exists — the write phase sometimes appends a descriptor to a resolved note's title, which
    // would otherwise leave a duplicate. The variant title becomes an alias on the base (via MergeNotes).
    public static int MergeTitleVariants()
    {
        int merged = 0;
        List<string> titles = Brain.GetTitles();
        HashSet<string> titleSet = new(titles, StringComparer.OrdinalIgnoreCase);
        foreach (string title in titles)
        {
            int cut = title.IndexOf(" — ", StringComparison.Ordinal);
            if (cut < 0) cut = title.IndexOf(" - ", StringComparison.Ordinal);
            if (cut <= 0) continue;
            string basePart = title[..cut].Trim();
            if (basePart.Length > 0 && !basePart.Equals(title, StringComparison.OrdinalIgnoreCase) && titleSet.Contains(basePart))
                if (Brain.MergeNotes(title, basePart)) merged++;
        }
        return merged;
    }

    public static int EnsureHubChildLinks()
    {
        int updated = 0;
        foreach (Note hub in Database.AllNotes())
        {
            if (!hub.HasChildren()) continue;
            string content = hub.Content;
            List<string> linked = Brain.GetWikilinks(content);
            List<Note> missing = hub.GetChildren()
                .Where(child => !linked.Contains(child.Title, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count == 0) continue;

            string additions = string.Join('\n', missing.Select(child => $"- [[{child.Title}]]"));
            content = content.Contains("## Members")
                ? content.Replace("## Members", $"## Members\n{additions}")
                : $"{content.TrimEnd()}\n\n## Members\n{additions}\n";
            Note.Write(hub.Path, content, hub.Aliases, null);
            updated++;
        }
        if (updated > 0) Brain.Index();
        return updated;
    }

    public static int CleanUnknownStubs()
    {
        int cleaned = 0;
        foreach (Note stub in Database.UnknownStubs())
        {
            Note? owner = Database.AliasOwner(stub.Title, stub.id);
            if (owner is null) continue;
            stub.MergeInto(owner);
            cleaned++;
        }
        return cleaned;
    }
}

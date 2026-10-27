using ARI.BrainVault;

namespace ARI.Scripting;

/// <summary>The brain, as scripts see it: names and text only, none of its internals.</summary>
internal static class ScriptBrain
{
    internal static IReadOnlyList<BrainHit> Search(IEnumerable<string> terms, int limit)
    {
        if (!Brain.Ready) return [];
        return Brain.Search(terms.ToList(), Math.Clamp(limit, 1, 200))
            .Select(r => new BrainHit(r.Note.Name, r.Note.Title, r.Score)).ToList();
    }

    internal static string? Read(string name)
    {
        if (!Brain.Ready) return null;
        try { return Brain.GetNote(name)?.Content; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}

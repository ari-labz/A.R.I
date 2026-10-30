using System.Text;
using System.Text.RegularExpressions;

namespace ARI.LLM;

// Skills are how-to notes Ari writes for herself: when she works out how to do something, she saves the steps and reads
// them next time instead of starting again. Plain markdown files, so the owner can read and edit them too.
internal static class SkillStore
{
    internal static string Folder => Path.Combine(ARI.Common.Paths.PersistentData, "Skills");
    internal static readonly Regex NameRx = new(@"^[a-z0-9][a-z0-9_-]{1,40}$", RegexOptions.Compiled);

    internal static string PathOf(string name) => Path.Combine(Folder, name + ".md");

    internal static List<(string Name, string Description)> All()
    {
        if (!Directory.Exists(Folder)) return new();
        return new DirectoryInfo(Folder).GetFiles("*.md").OrderBy(f => f.Name)
            .Select(f => (Path.GetFileNameWithoutExtension(f.Name), Describe(File.ReadAllText(f.FullName)))).ToList();
    }

    internal static string Describe(string text)
    {
        Match m = Regex.Match(text, @"^---\s*\n(?:.*\n)*?description:\s*(.*)\n(?:.*\n)*?---", RegexOptions.None);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    internal static string Body(string text) => Regex.Replace(text, @"^---\s*\n[\s\S]*?\n---\s*\n?", "").Trim();
}

[Tool("list_skills", "List the how-to notes you've saved for yourself, with what each is for. Check here before starting something you might have done before.", Access = ToolAccess.Read)]
public class ListSkills : Tool
{
    public override string OnValidate() => CallThread is { IsGuarded: false } ? null : "Skills belong to the owner's conversations.";
    public override Task<string> Run()
    {
        var all = SkillStore.All();
        return Task.FromResult(all.Count == 0 ? "You haven't saved any skills yet. When you work out how to do something worth repeating, save it with write_skill."
            : string.Join("\n", all.Select(s => $"- {s.Name}: {s.Description}")));
    }
}

[Tool("read_skill", "Read one of your saved how-to notes in full.", Access = ToolAccess.Read)]
public class ReadSkill : Tool
{
    [Param("The skill's name, as list_skills shows it.")]
    public string Name;

    public override string OnValidate() => CallThread is { IsGuarded: false } ? null : "Skills belong to the owner's conversations.";
    public override Task<string> Run()
    {
        string name = (Name ?? "").Trim().ToLowerInvariant();
        return Task.FromResult(SkillStore.NameRx.IsMatch(name) && File.Exists(SkillStore.PathOf(name))
            ? SkillStore.Body(File.ReadAllText(SkillStore.PathOf(name)))
            : $"[Error: no skill called '{name}'. Use list_skills to see what you have.]");
    }
}

[Tool("write_skill", "Save, or update, a how-to note for yourself, so you can do this again without working it out from scratch. " +
                     "Write it as steps a capable assistant could follow: what it's for, the commands or tools to use, and the mistakes to avoid. " +
                     "Save one whenever you work out something that took effort, or the user teaches you a way they like things done.")]
public class WriteSkill : Tool
{
    [Param("A short name: lowercase letters, numbers, - and _, e.g. make-xlsx-report.")]
    public string Name;

    [Param("One line on what it's for and when to use it. This is what you'll see in the list.")]
    public string Description;

    [Param("The note itself, in markdown.")]
    public string Content;

    public override string OnValidate()
    {
        if (CallThread is not { IsGuarded: false }) return "Skills belong to the owner's conversations.";
        return SkillStore.NameRx.IsMatch((Name ?? "").Trim().ToLowerInvariant()) ? null : "A skill name uses lowercase letters, numbers, - and _ (2 to 41 characters).";
    }

    public override Task<string> Run()
    {
        string name = Name.Trim().ToLowerInvariant();
        Directory.CreateDirectory(SkillStore.Folder);
        bool existed = File.Exists(SkillStore.PathOf(name));
        File.WriteAllText(SkillStore.PathOf(name), $"---\ndescription: {Description.Replace('\n', ' ').Trim()}\n---\n{Content.Trim()}\n");
        return Task.FromResult($"{(existed ? "Updated" : "Saved")} skill '{name}'.");
    }
}

[Tool("delete_skill", "Delete a saved how-to note that's wrong or no longer useful.")]
public class DeleteSkill : Tool
{
    [Param("The skill's name.")]
    public string Name;

    public override string OnValidate() => CallThread is { IsGuarded: false } ? null : "Skills belong to the owner's conversations.";
    public override Task<string> Run()
    {
        string name = (Name ?? "").Trim().ToLowerInvariant();
        if (!SkillStore.NameRx.IsMatch(name) || !File.Exists(SkillStore.PathOf(name))) return Task.FromResult($"[Error: no skill called '{name}'.]");
        File.Delete(SkillStore.PathOf(name));
        return Task.FromResult($"Deleted skill '{name}'.");
    }
}

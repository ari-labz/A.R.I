using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class CommandService
{
    private readonly Engram?                         engram;
    private readonly Refactor?                       refactor;
    private readonly Func<Task<int>>?                purgeNotes;
    private readonly Func<Task<string>>?             backupBrain;
    private readonly Func<Task<List<string>>>?       getDirtyNotes;

    internal CommandService(Engram? engram, Refactor? refactor = null, Func<Task<int>>? purgeNotes = null, Func<Task<string>>? backupBrain = null, Func<Task<List<string>>>? getDirtyNotes = null)
    {
        this.engram        = engram;
        this.refactor      = refactor;
        this.purgeNotes    = purgeNotes;
        this.backupBrain   = backupBrain;
        this.getDirtyNotes = getDirtyNotes;
    }

    internal async Task<string?> Handle(string input, string? threadKey = null)
    {
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith('/'))
            return null;

        string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string command  = parts[0].ToLowerInvariant();
        string sub      = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        return command switch
        {
            "/engram"        => await HandleEngram(sub, threadKey),
            "/refactor"      => await HandleRefactor(sub),
            "/purge"         => sub == "notes" ? await HandlePurge() : null,
            "/brain"         => sub == "backup" ? await HandleBackup() : null,
            "/getdirtynotes" => await HandleDirtyNotes(),
            _                => null
        };
    }

    private async Task<string> HandleEngram(string sub, string? threadKey)
    {
        if (engram is null) return "Engram is not loaded.";

        return sub switch
        {
            "on"     => Enable(),
            "off"    => Disable(),
            "sweep"  => await Sweep(threadKey),
            "status" => Status(),
            _        => "Unknown engram command. Options: `/engram on`, `/engram off`, `/engram sweep`, `/engram status`"
        };
    }

    private string Enable()
    {
        engram!.Enable();
        return "Engram enabled.";
    }

    private string Disable()
    {
        engram!.Disable();
        return "Engram disabled.";
    }

    private async Task<string> Sweep(string? threadKey)
    {
        if (!engram!.IsEnabled) return "Engram is disabled — sweep skipped.";
        if (threadKey is null)  return "No active thread — sweep skipped.";
        await engram.RunEngram(threadKey, "manual");
        return "Engram sweep complete.";
    }

    private string Status()
        => engram!.IsEnabled ? "Engram is currently **enabled**." : "Engram is currently **disabled**.";

    private async Task<string> HandleRefactor(string sub)
    {
        if (refactor is null) return "Refactor is not loaded.";
        bool allNotes = sub == "all";
        Common.Logger.LogInformation("[Commands] Refactor requested (mode: {Mode}).", allNotes ? "all" : "dirty");
        return await refactor.Run(allNotes);
    }

    private async Task<string> HandlePurge()
    {
        if (purgeNotes is null) return "Brain is not available.";
        Common.Logger.LogInformation("[Commands] Brain purge requested.");
        int deleted = await purgeNotes();
        return $"Purged {deleted} note(s) from the brain.";
    }

    private async Task<string> HandleBackup()
    {
        if (backupBrain is null) return "Brain is not available.";
        Common.Logger.LogInformation("[Commands] Brain backup requested.");
        return await backupBrain();
    }

    private async Task<string> HandleDirtyNotes()
    {
        if (getDirtyNotes is null) return "Brain is not available.";
        List<string> dirty = await getDirtyNotes();
        if (dirty.Count == 0) return "No dirty notes — graph is clean.";
        return $"**{dirty.Count} dirty note(s):**\n" + string.Join("\n", dirty.Select(n => $"- {n}"));
    }
}

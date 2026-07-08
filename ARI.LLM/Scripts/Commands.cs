using ARI.Brain;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

internal class CommandService
{
    private readonly Engram?   engram;
    private readonly Refactor? refactor;

    internal CommandService(Engram? engram, Refactor? refactor = null)
    {
        this.engram   = engram;
        this.refactor = refactor;
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
            "/purge"         => sub == "notes" ? HandlePurge() : null,
            "/brain"         => sub == "backup" ? HandleBackup() : null,
            "/getdirtynotes" => HandleDirtyNotes(),
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
        if (sub == "once")   // eval: a single epoch, for fast iteration
        {
            Shared.Logger.LogInformation("[Commands] Refactor requested (mode: once — single epoch).");
            return await refactor.Run(allNotes: true, epochsOverride: 1);
        }
        bool allNotes = sub == "all";
        Shared.Logger.LogInformation("[Commands] Refactor requested (mode: {Mode}).", allNotes ? "all" : "dirty");
        return await refactor.Run(allNotes);
    }

    private static string HandlePurge()
    {
        if (!BrainModule.Ready) return "Brain is not available.";
        Shared.Logger.LogInformation("[Commands] Brain purge requested.");
        int deleted = BrainModule.PurgeAllNotes();
        return $"Purged {deleted} note(s) from the brain.";
    }

    private static string HandleBackup()
    {
        if (!BrainModule.Ready) return "Brain is not available.";
        Shared.Logger.LogInformation("[Commands] Brain backup requested.");
        return BrainModule.Backup();
    }

    private static string HandleDirtyNotes()
    {
        if (!BrainModule.Ready) return "Brain is not available.";
        List<string> dirty = BrainModule.GetDirtyNotes();
        if (dirty.Count == 0) return "No dirty notes — graph is clean.";
        return $"**{dirty.Count} dirty note(s):**\n" + string.Join("\n", dirty.Select(n => $"- {n}"));
    }
}

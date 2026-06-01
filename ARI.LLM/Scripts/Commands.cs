using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// Central handler for all ARI slash commands.
/// Both Discord and the web panel pass input here — this is the single source of truth for command logic.
/// </summary>
internal class CommandService
{
    private readonly Engram? engram;
    private readonly Func<Task<int>>?    purgeNotes;
    private readonly Func<Task<string>>? backupBrain;

    internal CommandService(Engram? engram, Func<Task<int>>? purgeNotes = null, Func<Task<string>>? backupBrain = null)
    {
        this.engram      = engram;
        this.purgeNotes  = purgeNotes;
        this.backupBrain = backupBrain;
    }

    /// <summary>
    /// Parses and executes a slash command.
    /// Returns a human-readable result string, or null if the input is not a recognised command.
    /// </summary>
    internal async Task<string?> HandleAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith('/'))
            return null;

        string[] parts  = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string command   = parts[0].ToLowerInvariant();
        string sub       = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        return command switch
        {
            "/engram" => await HandleEngramAsync(sub),
            "/purge"  => sub == "notes" ? await HandlePurgeNotesAsync() : null,
            "/brain"  => sub == "backup" ? await HandleBrainBackupAsync() : null,
            _         => null
        };
    }

    // ── /engram ───────────────────────────────────────────────────────────────────

    private async Task<string> HandleEngramAsync(string sub)
    {
        if (engram is null) return "Engram is not loaded.";

        return sub switch
        {
            "on"      => EngramEnable(),
            "off"     => EngramDisable(),
            "sweep"   => await EngramSweepAsync(),
            "status"  => EngramStatus(),
            "refactor" => await EngramRefactorAsync(),
            _         => "Unknown engram command. Options: `/engram on`, `/engram off`, `/engram sweep`, `/engram status`, `/engram refactor`"
        };
    }

    private string EngramEnable()
    {
        engram!.Enable();
        return "Engram enabled.";
    }

    private string EngramDisable()
    {
        engram!.Disable();
        return "Engram disabled.";
    }

    private async Task<string> EngramSweepAsync()
    {
        await engram!.ManualSweepAsync();
        return engram.IsEnabled ? "Engram sweep complete." : "Engram is disabled — sweep skipped.";
    }

    private string EngramStatus()
        => engram!.IsEnabled ? "Engram is currently **enabled**." : "Engram is currently **disabled**.";

    private async Task<string> EngramRefactorAsync()
    {
        Common.Logger.LogInformation("[Commands] Graph refactor requested.");
        return await engram!.RefactorAsync();
    }

    // ── /purge notes ──────────────────────────────────────────────────────────────

    private async Task<string> HandlePurgeNotesAsync()
    {
        if (purgeNotes is null) return "Brain is not available.";
        Common.Logger.LogInformation("[Commands] Brain purge requested.");
        int deleted = await purgeNotes();
        return $"Purged {deleted} note(s) from the brain.";
    }

    // ── /brain backup ─────────────────────────────────────────────────────────────

    private async Task<string> HandleBrainBackupAsync()
    {
        if (backupBrain is null) return "Brain is not available.";
        Common.Logger.LogInformation("[Commands] Brain backup requested.");
        return await backupBrain();
    }
}

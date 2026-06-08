namespace ARI.WebPanel;

/// <summary>
/// Lets AriHostService inject a Discord DM callback into the web panel
/// without creating a circular project reference.
/// </summary>
public class DiscordServiceHolder
{
    private Func<string, Task>? _notifyOwner;

    public void Set(Func<string, Task> notifyOwner) => _notifyOwner = notifyOwner;

    public async Task NotifyOwner(string message)
    {
        if (_notifyOwner is null) return;
        try { await _notifyOwner(message); }
        catch { /* best-effort */ }
    }
}

using ARI.Common;

namespace ARI.LLM;

/// <summary>
/// Appends summary-level records for each dream session to a timestamped file under DreamLogs/.
/// Tool names only — no arguments, no results, no inner monologue.
/// </summary>
internal sealed class DreamLog
{
    private static string LogDir => Path.Combine(Paths.PersistentData, "Logs", "DreamLogs");

    private string?       path;
    private DateTime      sessionStart;
    private List<string>  toolsCalled = new();
    private bool          woke;
    private string        wakeContent = "";

    internal void Begin(string threadKey)
    {
        sessionStart = DateTime.Now;
        toolsCalled  = new List<string>();
        woke         = false;
        wakeContent  = "";

        Directory.CreateDirectory(LogDir);
        path = Path.Combine(LogDir, $"dream_{sessionStart:yyyy-MM-dd_HH-mm-ss}.log");
        Append($"[{sessionStart:yyyy-MM-dd HH:mm:ss}] Dream session started (thread: {threadKey})");
    }

    internal void RecordAnchor(string description)
        => Append($"[{Now}] Anchor: {description}");

    internal void RecordToolCall(string toolName)
    {
        toolsCalled.Add(toolName);
        Append($"[{Now}] [tool call: {toolName}]");
    }

    internal void RecordWake(string content)
    {
        woke        = true;
        wakeContent = content;
        Append($"[{Now}] Wake fired");
        Append($"  Content: \"{content}\"");
    }

    internal void End(string threadKey)
    {
        TimeSpan duration = DateTime.Now - sessionStart;
        Append($"[{Now}] Dream session ended");
        Append($"  Duration: {duration:m\\m\\ ss\\s}");
        Append($"  Wake: {(woke ? "yes" : "no")}");
    }

    private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void Append(string line)
    {
        if (path is null) return;
        try { File.AppendAllText(path, line + "\n"); } catch { /* non-fatal */ }
    }
}

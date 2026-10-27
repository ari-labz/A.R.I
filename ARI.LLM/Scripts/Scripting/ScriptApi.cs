using System.Net.Http;

namespace ARI.Scripting;

/// <summary>What a script file is written against: one public class that inherits from <see cref="ScriptNode"/>. Its
/// <see cref="InputAttribute"/> properties become the node's input ports and its <see cref="OutputAttribute"/> properties its output
/// ports. Every port carries text. The script is compiled when it is saved and the node runs the new version from then on.
/// Scripts are trusted code: they run inside A·R·I with the same access as A·R·I itself.</summary>
public abstract class ScriptNode
{
    /// <summary>One line on what the node does, shown in the editor.</summary>
    public virtual string Description => "";

    /// <summary>How long a run may take before the pipeline gives up on it.</summary>
    public virtual int TimeoutSeconds => 30;

    /// <summary>Does the node's work: read the input properties, set the output properties.</summary>
    public abstract Task Run(ScriptContext ctx);
}

/// <summary>Marks a text property as an input port. A required input must have something wired in; an optional one is left
/// null when nothing is.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class InputAttribute : Attribute
{
    public string Description { get; set; } = "";
    public bool Optional { get; set; }
}

/// <summary>Marks a text property as an output port.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OutputAttribute : Attribute
{
    public string Description { get; set; } = "";
}

/// <summary>A note found in the brain.</summary>
public sealed record BrainHit(string Name, string Title, double Score);

/// <summary>What a running script can reach beyond its own inputs. Kept small and stable on purpose: scripts that use only this
/// keep working when the inside of A·R·I changes.</summary>
public sealed class ScriptContext
{
    private readonly Func<string, string, CancellationToken, Task<string>>? ask;
    internal Func<string, string, CancellationToken, Task<string>>? AskDelegate => ask;
    private readonly Action<string> log;

    public ScriptContext(string threadKey, string username, CancellationToken ct, Action<string> log,
        Func<string, string, CancellationToken, Task<string>>? ask)
    {
        ThreadKey = threadKey; Username = username; Ct = ct; this.log = log; this.ask = ask;
    }

    /// <summary>The conversation this run belongs to.</summary>
    public string ThreadKey { get; }

    /// <summary>Who sent the latest message.</summary>
    public string Username { get; }

    /// <summary>Cancelled when the user stops the reply or the node times out. Pass it to anything that waits.</summary>
    public CancellationToken Ct { get; }

    /// <summary>Writes a line to A·R·I's log, tagged with the script's name.</summary>
    public void Log(string message) => log(message);

    /// <summary>Sends a prompt to one of your custom agents (it runs on its own server and slot) and returns its answer.</summary>
    public Task<string> Ask(string agentName, string prompt) =>
        ask is null ? throw new InvalidOperationException("Ask isn't available here.") : ask(agentName, prompt, Ct);

    /// <summary>A shared HTTP client for calling out.</summary>
    public HttpClient Http { get; } = SharedHttp;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Searches the brain by keyword and returns the best matching notes.</summary>
    public IReadOnlyList<BrainHit> SearchBrain(IEnumerable<string> terms, int limit = 25) => ScriptBrain.Search(terms, limit);

    /// <summary>The full text of a brain note, or null if there is no such note.</summary>
    public string? ReadNote(string name) => ScriptBrain.Read(name);
}

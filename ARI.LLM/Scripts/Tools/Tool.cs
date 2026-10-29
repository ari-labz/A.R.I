using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Base class for ARI's built-in tools. A tool owns its own schema, execution and
/// display markers, and registers itself onto a thread through the same public
/// <see cref="Thread.RegisterTool"/> extension point that external clients use.
/// </summary>
internal enum ToolAccess { Read, Write, ReadWrite }

internal abstract class Tool
{
    internal abstract string     Name   { get; }
    internal abstract object     Schema { get; }
    internal virtual  ToolAccess Access => ToolAccess.ReadWrite;

    /// <summary>One-line description extracted from the schema's function.description field.</summary>
    internal string SchemaDescription
    {
        get
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(Schema));
                if (doc.RootElement.TryGetProperty("function", out JsonElement fn) &&
                    fn.TryGetProperty("description", out JsonElement desc))
                    return desc.GetString() ?? string.Empty;
            }
            catch { }
            return string.Empty;
        }
    }

    internal abstract Task<ToolResult> Execute(string argsJson);

    /// <summary>Marker emitted when the call is made. Null = no marker.</summary>
    internal virtual Func<string, string>?  Display          => null;
    /// <summary>Marker emitted after a successful call. Null = none.</summary>
    internal virtual Func<string, string>?  DisplayAfter     => null;
    /// <summary>Live marker updated as the call's arguments stream in. Null = none.</summary>
    internal virtual Func<string, string?>? StreamingDisplay => null;

    // Veto hooks — return a message to block the call, null to allow.
    // StreamingPreCheck fires on every args delta; PreCheck fires once post-stream before Execute.
    // Both bubble: tool → agent → pipeline. Most tools leave these as null.
    internal virtual string? StreamingPreCheck(Thread thread, string partialArgs) => null;
    internal virtual string? PreCheck(Thread thread, string argsJson) => null;

    /// <summary>Intercepts the result after Run and before it returns to the model — the place for shared
    /// post-processing a Run body shouldn't carry: truncating oversized text, rejecting a decode that blew
    /// up in size. A whole tool family (e.g. every read) can share one override. Default passes through.</summary>
    internal virtual ToolResult PostRun(Thread thread, string argsJson, ToolResult result) => result;

    /// <summary>Applies the control panel's settings at call time: a tool switched off since it was loaded refuses,
    /// and an async one starts in the background and returns at once (its result arrives later; see
    /// <see cref="AsyncToolJobs"/>). Internal threads (subagents) have no one to report to, so they run it inline.</summary>
    private Task<ToolResult> Dispatch(Thread thread, string argsJson)
    {
        if (!ToolRegistry.IsEnabled(Name))
            return Task.FromResult<ToolResult>($"[Error: the {Name} tool has been switched off in the control panel.]");
        if (!ToolRegistry.IsAsync(Name) || thread.Internal) return Execute(argsJson);
        return Task.FromResult(AsyncToolJobs.Start(thread, this, argsJson));
    }

    internal void Register(Thread thread)
    {
        thread.toolAccess[Name] = Access;
        thread.RegisterTool(Name, Schema, argsJson => Dispatch(thread, argsJson), Display, DisplayAfter, StreamingDisplay,
            partialArgs => StreamingPreCheck(thread, partialArgs),
            argsJson    => PreCheck(thread, argsJson),
            (argsJson, result) => PostRun(thread, argsJson, result));
    }
}

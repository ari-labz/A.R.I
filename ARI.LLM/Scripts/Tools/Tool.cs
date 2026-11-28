using System.Reflection;
using System.Text.Json;

namespace ARI.LLM;

/// <summary>
/// Base class for ARI's tools, built-in and custom. A built-in owns its own schema, execution and display markers
/// (the internal members below) and registers itself onto a thread through the same public
/// <see cref="Thread.RegisterTool"/> extension point that external clients use. A custom tool, written as a C#
/// script, overrides the public lifecycle methods in ToolBehaviour.cs instead and gets its schema from attributes.
/// Nothing here is abstract, so a class outside this assembly can inherit it.
/// </summary>
public enum ToolAccess { Read, Write, ReadWrite }

public abstract partial class Tool
{
    private ToolAttribute? Meta => GetType().GetCustomAttribute<ToolAttribute>();

    internal virtual string      Name   => Meta?.Name ?? GetType().Name;
    internal virtual object      Schema => Meta is { } m
        ? ToolParams.Schema(m.Name, m.Description, ToolParams.Describe(GetType()))
        : throw new NotSupportedException($"{GetType().Name} doesn't describe itself; mark it with [Tool] and [Param].");
    internal virtual ToolAccess  Access => Meta?.Access ?? ToolAccess.ReadWrite;

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

    // A tool written with [Tool]/[Param] needs no Execute: each call gets a fresh instance (so parameters never leak
    // between calls), is bound, and runs through the lifecycle with the tool's time limit.
    internal virtual Task<ToolResult> Execute(string argsJson) => RunGuarded(argsJson, Meta?.TimeoutSeconds ?? 120, Name);

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

    /// <summary>What the model is told when one result from this tool is too big to fit its context. The host decides
    /// when (a share of the slot's context, for every tool); a tool only overrides the wording, so it can say what
    /// to narrow ("this search matched 4,812 lines (3.1 MB). Search a smaller folder or a more specific pattern").</summary>
    internal virtual string TooLarge(string argsJson, ToolSize size) => DefaultTooLarge(Name, size);

    internal static string DefaultTooLarge(string name, ToolSize size) => name switch
    {
        _ when name.Contains("search") || name.Contains("grep") || name.Contains("find") =>
            $"This search matched {size.Describe()}, too much to show. Search again with a more specific pattern or a smaller folder.",
        _ when name.Contains("read") || name.Contains("fetch") =>
            $"This was {size.Describe()}, too much to show at once. Read it in parts (a line range or a section).",
        "run_command" => $"This command printed {size.Describe()}, too much to show. Run it again with its output filtered (head, tail, grep) or sent to a file.",
        _ => $"This {name} result was {size.Describe()}, too big to show in full. Ask for less: a narrower scope, a more specific query, or a range.",
    };

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
        thread.toolTooLarge[Name] = TooLarge;
        thread.RegisterTool(Name, Schema, argsJson => Dispatch(thread, argsJson), Display, DisplayAfter, StreamingDisplay,
            partialArgs => StreamingPreCheck(thread, partialArgs),
            argsJson    => PreCheck(thread, argsJson),
            (argsJson, result) => PostRun(thread, argsJson, result));
    }
}

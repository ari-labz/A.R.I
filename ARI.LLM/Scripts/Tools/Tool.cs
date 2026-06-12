namespace ARI.LLM;

/// <summary>
/// Base class for ARI's built-in tools. A tool owns its own schema, execution and
/// display markers, and registers itself onto a thread through the same public
/// <see cref="Thread.RegisterTool"/> extension point that external clients use.
/// </summary>
internal abstract class Tool
{
    internal abstract string Name   { get; }
    internal abstract object Schema { get; }

    internal abstract Task<string> Execute(string argsJson);

    /// <summary>Marker emitted when the call is made. Null = no marker.</summary>
    internal virtual Func<string, string>?  Display          => null;
    /// <summary>Marker emitted after a successful call. Null = none.</summary>
    internal virtual Func<string, string>?  DisplayAfter     => null;
    /// <summary>Live marker updated as the call's arguments stream in. Null = none.</summary>
    internal virtual Func<string, string?>? StreamingDisplay => null;

    internal void Register(Thread thread)
        => thread.RegisterTool(Name, Schema, Execute, Display, DisplayAfter, StreamingDisplay);
}

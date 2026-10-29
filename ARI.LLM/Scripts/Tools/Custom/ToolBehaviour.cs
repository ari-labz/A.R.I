using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

// What a custom tool overrides. Every method has a default, so a tool writes only what it needs, and only Run
// is ever required. A fresh instance is made for every call, so fields never leak between calls — use static
// fields for anything that should persist.
//
//   bind [Param] members → OnValidate → OnStart → Run → OnComplete       (or OnError / OnCancel)
public abstract partial class Tool
{
    internal Thread?          CallThread { get; set; }
    internal CancellationToken CallToken  { get; set; }

    /// <summary>The work. Whatever text this returns is what Ari reads.</summary>
    public virtual Task<string> Run() => Task.FromResult("[Error: this tool has no Run method.]");

    /// <summary>Called before Run, once the parameters are filled in. Return a message to refuse the call (Ari sees it), or null to go ahead.</summary>
    public virtual string? OnValidate() => null;

    /// <summary>Called just before Run.</summary>
    public virtual void OnStart() { }

    /// <summary>Called with what Run returned. Return the text Ari should get, which can be a tidied-up version.</summary>
    public virtual string OnComplete(string result) => result;

    /// <summary>Called when Run throws or runs out of time. Return what Ari should read instead.</summary>
    public virtual string OnError(Exception error) => $"[Error: {error.Message}]";

    /// <summary>Called when the call is stopped part-way, so you can clean up.</summary>
    public virtual void OnCancel() { }

    /// <summary>For web requests. Shared by all tools.</summary>
    protected static HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(100) };

    /// <summary>Fires when the call is cancelled or times out. Pass it to anything slow.</summary>
    protected CancellationToken Cancelled => CallToken;

    /// <summary>True when the person Ari is talking to is you, the admin.</summary>
    protected bool IsAdmin => CallThread?.IsAdminChat ?? false;

    /// <summary>Which conversation this call belongs to.</summary>
    protected string ConversationId => CallThread?.Key ?? "";

    /// <summary>A folder only this tool uses, created on first use. Keep its files here.</summary>
    protected string DataFolder
    {
        get
        {
            string dir = Path.Combine(Paths.PersistentData, "CustomTools", "data", GetType().Name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Writes to ARI's system log.</summary>
    protected void Log(string message) => Shared.Logger.LogInformation("[CustomTool {Tool}] {Message}", GetType().Name, message);

    /// <summary>The folder this tool should work in: the conversation's project or scratchpad, made on first use.</summary>
    protected string WorkingFolder
    {
        get
        {
            if (CallThread is null) return Directory.GetCurrentDirectory();
            if (CallThread.FilesystemRoot is { Length: > 0 } root) return root;
            string dir = Paths.ScratchpadDir(CallThread.Key);
            Directory.CreateDirectory(dir);
            CallThread.FilesystemRoot = dir;
            return dir;
        }
    }

    /// <summary>Runs a fresh instance of this tool for one call. A tool set to run in the background isn't tied to the
    /// reply that started it or to a time limit; any other is stopped when the reply is, or when it runs out of time.</summary>
    internal async Task<ToolResult> RunGuarded(string argsJson, int timeoutSeconds, string toolName)
    {
        Tool instance;
        try { instance = (Tool)Activator.CreateInstance(GetType(), nonPublic: true)!; }
        catch (Exception e) { return $"[Error: couldn't start {toolName}: {e.Message}]"; }
        instance.CallThread = CallThread;

        if (ToolRegistry.IsAsync(toolName) && CallThread is { Internal: false })
            return await instance.RunLifecycle(argsJson, CancellationToken.None);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(CallThread?.Ct ?? CancellationToken.None);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));
        return await instance.RunLifecycle(argsJson, cts.Token);
    }

    internal async Task<ToolResult> RunLifecycle(string argsJson, CancellationToken ct)
    {
        CallToken = ct;
        try
        {
            ToolParams.Bind(this, argsJson);
            if (OnValidate() is { } refusal) return refusal;
            OnStart();
            return OnComplete(await Run() ?? "");
        }
        catch (ToolArgException e) { return $"[Error: {e.Message}]"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { OnCancel(); } catch (Exception e) { Shared.Logger.LogWarning(e, "[CustomTool {Tool}] OnCancel threw.", GetType().Name); }
            return "[Cancelled]";
        }
        catch (Exception e)
        {
            Shared.Logger.LogWarning(e, "[CustomTool {Tool}] failed.", GetType().Name);
            try { return OnError(e); } catch { return $"[Error: {e.Message}]"; }
        }
    }
}

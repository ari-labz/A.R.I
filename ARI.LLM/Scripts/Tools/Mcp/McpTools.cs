using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ARI.Common;
using Microsoft.Extensions.Logging;

namespace ARI.LLM;

/// <summary>
/// MCP servers: the tool servers other AI apps share. Each one in McpServers.json (data folder) is started as a program ARI talks to over
/// its stdin and stdout (MCP's stdio transport, JSON-RPC one message per line). Every tool it offers becomes one of Ari's tools, named
/// mcp_&lt;server&gt;_&lt;tool&gt;, for the owner only. Edited and reloaded from the control panel's Tools tab.
/// </summary>
internal static class McpTools
{
    internal sealed record ServerConfig(string Name, string Command, List<string>? Args, Dictionary<string, string>? Env, bool Enabled = true);
    private sealed record Config(List<ServerConfig> Servers);

    internal static string ConfigPath => Path.Combine(Paths.PersistentData, "McpServers.json");
    private static readonly ConcurrentDictionary<string, McpServer> servers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (McpServer Server, McpServer.ToolInfo Tool)> byName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly SemaphoreSlim reloading = new(1, 1);

    internal static IEnumerable<string> Names => byName.Keys.ToList();
    internal static bool IsMcp(string name) => byName.ContainsKey(name);

    internal static Tool? Create(string name) => byName.TryGetValue(name, out var t) ? new McpTool(name, t.Server, t.Tool) : null;

    /// <summary>Each configured server and how it is doing, for the control panel.</summary>
    internal static List<object> Status() => servers.Values.Select(s => (object)new { name = s.Name, state = s.State, tools = s.Tools.Select(t => t.Name).ToList() }).ToList();

    internal static string ReadConfig() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : JsonSerializer.Serialize(new Config(new()), Json);

    /// <summary>Saves the config after checking it parses. Returns null on success, otherwise why not.</summary>
    internal static string? SaveConfig(string text)
    {
        try { if (JsonSerializer.Deserialize<Config>(text, Json) is null) return "It's empty."; }
        catch (JsonException e) { return "That isn't valid JSON: " + e.Message; }
        Directory.CreateDirectory(Paths.PersistentData);
        File.WriteAllText(ConfigPath, text);
        return null;
    }

    /// <summary>Stops every running server and starts the enabled ones from the config again.</summary>
    internal static async Task ReloadAsync()
    {
        await reloading.WaitAsync();
        try
        {
            foreach (McpServer s in servers.Values) s.Dispose();
            servers.Clear(); byName.Clear();
            if (!File.Exists(ConfigPath)) return;
            Config? config;
            try { config = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json); }
            catch (JsonException e) { Shared.Logger.LogWarning("[MCP] McpServers.json isn't valid: {Err}", e.Message); return; }
            foreach (ServerConfig sc in config?.Servers ?? new())
            {
                if (!sc.Enabled || string.IsNullOrWhiteSpace(sc.Name) || string.IsNullOrWhiteSpace(sc.Command)) continue;
                McpServer server = new(sc);
                servers[sc.Name] = server;
                try
                {
                    await server.StartAsync();
                    foreach (McpServer.ToolInfo tool in server.Tools) byName[ToolName(sc.Name, tool.Name)] = (server, tool);
                    Shared.Logger.LogInformation("[MCP] {Server}: {Count} tool(s).", sc.Name, server.Tools.Count);
                }
                catch (Exception e) { server.State = "failed: " + e.Message.Split('\n')[0]; Shared.Logger.LogWarning("[MCP] {Server} didn't start: {Err}", sc.Name, e.Message); }
            }
        }
        finally { reloading.Release(); }
    }

    private static string ToolName(string server, string tool)
    {
        string name = "mcp_" + Regex.Replace($"{server}_{tool}".ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        return name.Length <= 64 ? name : name[..64];
    }
}

/// <summary>One MCP server process and the JSON-RPC conversation with it.</summary>
internal sealed class McpServer(McpTools.ServerConfig config) : IDisposable
{
    internal sealed record ToolInfo(string Name, string Description, JsonNode Schema);

    internal string Name => config.Name;
    internal string State { get; set; } = "starting";
    internal List<ToolInfo> Tools { get; private set; } = new();

    private Process? process;
    private int nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    internal async Task StartAsync()
    {
        ProcessStartInfo psi = new(config.Command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in config.Args ?? new()) psi.ArgumentList.Add(a);
        foreach ((string k, string v) in config.Env ?? new()) psi.Environment[k] = v;
        process = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start " + config.Command);
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Shared.Logger.LogDebug("[MCP {Server}] {Line}", Name, e.Data); };
        process.BeginErrorReadLine();
        _ = Task.Run(ReadLoop);

        await Request("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "ARI", version = BuildInfo.Version } }, TimeSpan.FromSeconds(30));
        await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
        JsonElement list = await Request("tools/list", new { }, TimeSpan.FromSeconds(30));
        Tools = list.TryGetProperty("tools", out JsonElement tools)
            ? tools.EnumerateArray().Select(t => new ToolInfo(
                t.GetProperty("name").GetString() ?? "",
                t.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : "",
                JsonNode.Parse(t.TryGetProperty("inputSchema", out JsonElement s) ? s.GetRawText() : "{\"type\":\"object\",\"properties\":{}}")!)).Where(t => t.Name.Length > 0).ToList()
            : new();
        State = "running";
    }

    internal Task<JsonElement> Call(string tool, JsonElement arguments) => Request("tools/call", new { name = tool, arguments }, CallTimeout);

    private async Task<JsonElement> Request(string method, object @params, TimeSpan timeout)
    {
        int id = Interlocked.Increment(ref nextId);
        TaskCompletionSource<JsonElement> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        await Send(new { jsonrpc = "2.0", id, method, @params });
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeout)) != tcs.Task) { pending.TryRemove(id, out _); throw new TimeoutException($"{Name} didn't answer {method} in {timeout.TotalSeconds:0}s."); }
        return await tcs.Task;
    }

    private async Task Send(object message)
    {
        if (process is null || process.HasExited) throw new InvalidOperationException($"{Name} isn't running.");
        await writing.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await process.StandardInput.FlushAsync(); }
        finally { writing.Release(); }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (process is { HasExited: false } && await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0) continue;
                JsonElement msg;
                try { msg = JsonDocument.Parse(line).RootElement.Clone(); } catch { continue; }
                bool hasId = msg.TryGetProperty("id", out JsonElement idEl);
                if (msg.TryGetProperty("method", out JsonElement method))
                {
                    // A request from the server (ping, roots/list, sampling…): answer what's harmless, refuse the rest.
                    if (!hasId) continue;
                    object reply = method.GetString() switch
                    {
                        "ping"       => new { jsonrpc = "2.0", id = idEl, result = new { } },
                        "roots/list" => new { jsonrpc = "2.0", id = idEl, result = new { roots = Array.Empty<object>() } },
                        _            => new { jsonrpc = "2.0", id = idEl, error = new { code = -32601, message = "ARI doesn't support that." } },
                    };
                    try { await Send(reply); } catch { }
                    continue;
                }
                if (!hasId || !idEl.TryGetInt32(out int id) || !pending.TryRemove(id, out var tcs)) continue;
                if (msg.TryGetProperty("error", out JsonElement err)) tcs.TrySetException(new InvalidOperationException(err.TryGetProperty("message", out JsonElement m) ? m.GetString() : err.GetRawText()));
                else tcs.TrySetResult(msg.TryGetProperty("result", out JsonElement r) ? r : default);
            }
        }
        catch { }
        State = "stopped";
        foreach (var p in pending.Values) p.TrySetException(new InvalidOperationException($"{Name} stopped."));
    }

    public void Dispose()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        process?.Dispose();
    }
}

/// <summary>One MCP server's tool, as one of Ari's tools.</summary>
internal sealed class McpTool(string name, McpServer server, McpServer.ToolInfo info) : Tool
{
    internal override string Name => name;
    internal override object Schema => new
    {
        type = "function",
        function = new { name, description = $"[{server.Name}] {info.Description}".Trim(), parameters = info.Schema },
    };

    internal override async Task<ToolResult> Execute(string argsJson)
    {
        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException) { return (ToolResult)"[Error: the arguments aren't valid JSON.]"; }
        JsonElement result;
        try { result = await server.Call(info.Name, args); }
        catch (Exception e) { return (ToolResult)$"[Error: {server.Name}: {e.Message}]"; }

        StringBuilder text = new();
        byte[]? image = null; string imageType = "";
        if (result.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
            foreach (JsonElement part in content.EnumerateArray())
            {
                string type = part.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                if (type == "text" && part.TryGetProperty("text", out JsonElement tx)) text.AppendLine(tx.GetString());
                else if (type == "image" && image is null && part.TryGetProperty("data", out JsonElement data))
                {
                    try { image = Convert.FromBase64String(data.GetString() ?? ""); imageType = part.TryGetProperty("mimeType", out JsonElement mt) ? mt.GetString() ?? "image/png" : "image/png"; } catch { }
                }
                else if (type == "resource" && part.TryGetProperty("resource", out JsonElement res) && res.TryGetProperty("text", out JsonElement rt)) text.AppendLine(rt.GetString());
            }
        bool isError = result.TryGetProperty("isError", out JsonElement ie) && ie.ValueKind == JsonValueKind.True;
        string body = text.ToString().Trim();
        if (isError) return (ToolResult)$"Error: {(body.Length > 0 ? body : "the tool reported an error")}";
        if (image is not null) return ToolResult.AsImage(image, imageType, body);
        return (ToolResult)(body.Length > 0 ? body : "Done (no output).");
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ARI.Common;
using ARI.LLM;

namespace ARI.API.Data;

public sealed class SharedPromptsFile
{
    public Dictionary<string, string>? MemoryAgent { get; set; }
    public Dictionary<string, string>? ToolSystem  { get; set; }
}

public sealed class AgentDefinition
{
    public string  Name              { get; set; } = "";
    public string  ServerName        { get; set; } = "";
    public string  AgentPrompt       { get; set; } = "";
    public Dictionary<string, string>? PromptTemplates { get; set; }
    // Modelled here or Save() drops them: the Coder's per-phase prompts, and Curiosity's rulebook opt-out.
    public Dictionary<string, PhaseConfig>? Phases { get; set; }
    public bool?   UseGraphRulebook  { get; set; }
    public bool    Enabled           { get; set; } = true;
    // Made in the control panel rather than shipped: the only agents that can be deleted.
    public bool    Custom            { get; set; }
    public string? SlotName          { get; set; }
    public bool    Think             { get; set; }
    public int     BudgetThinking    { get; set; }
    public int     BudgetResponse         { get; set; } = -1;
    public int     MaxToolCalls      { get; set; }
    // BudgetContext is no longer settable here — it's derived from the agent's bound slot (Server.cs
    // NamedSlot.ContextLimit), not agent config. See Agent.BudgetContext.
    public bool             NativeTools       { get; set; }
    public SamplerSettings? SamplerSettings    { get; set; }
    public bool    SupportsCompaction { get; set; }
    public int     CompactHighPct    { get; set; } = 80;
    public int     CompactLowPct     { get; set; } = 60;
    // Dialogue-specific
    public int?    ShortTermMemoryLimit { get; set; }
    // Engram-specific
    public int?    SweepIntervalMinutes { get; set; }
    public int?    RecursiveBrainSearchDepth { get; set; }
    // Exchanges before an interval sweep; 0/null disables it. Control panel Brain tab slider.
    public int?    TurnsBeforeSweep     { get; set; }
}

/// <summary>
/// Persists LLM data across three files under AppDataRoot/Server/:
///   Servers.json, Models.json, Agents.json
/// All mutations go through this class. Thread-safe per-file.
/// </summary>
public class PersistentData
{
    private sealed class ServersFile
    {
        public List<Server> Servers { get; set; } = new();
    }

    private sealed class ModelsFile
    {
        public List<Model>                Models     { get; set; } = new();
        public Dictionary<string, string> ModelNotes { get; set; } = new();
    }

    private sealed class AgentsFile
    {
        // Must be modelled here: Save() rewrites the whole file, so anything absent from this class is
        // dropped the first time an agent is edited.
        public SharedPromptsFile?    Shared { get; set; }
        public List<AgentDefinition> Agents { get; set; } = new();
    }

    private sealed class VoiceFile
    {
        public Dictionary<string, string> DefaultModels { get; set; } = new();
    }

    // ────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dir;
    private readonly string _serversPath;
    private readonly string _modelsPath;
    private readonly string _agentsPath;
    private readonly string _voicePath;

    private readonly object _serversLock = new();
    private readonly object _modelsLock  = new();
    private readonly object _agentsLock  = new();
    private readonly object _voiceLock   = new();

    public PersistentData()
    {
        _dir = Paths.PersistentData;

        _serversPath = Path.Combine(_dir, "Servers.json");
        _modelsPath  = Path.Combine(_dir, "Models.json");
        _agentsPath  = Path.Combine(_dir, "Agents.json");
        _voicePath   = Path.Combine(_dir, "Voice.json");

    }

    // ── Servers ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<Server> GetServers()
    {
        lock (_serversLock) return LoadServers().Servers;
    }

    public Server? GetServer(Guid id)
    {
        lock (_serversLock) return LoadServers().Servers.FirstOrDefault(s => s.Id == id);
    }

    public Server? GetServerByName(string name)
    {
        lock (_serversLock) return LoadServers().Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public Server AddServer(Server server)
    {
        lock (_serversLock)
        {
            ServersFile data = LoadServers();
            data.Servers.Add(server);
            Save(_serversPath, data);
            return server;
        }
    }

    public bool UpdateServer(Server updated)
    {
        lock (_serversLock)
        {
            ServersFile data = LoadServers();
            int idx = data.Servers.FindIndex(s => s.Id == updated.Id);
            if (idx < 0) return false;
            data.Servers[idx] = updated;
            Save(_serversPath, data);
            return true;
        }
    }

    public bool RemoveServer(Guid id)
    {
        lock (_serversLock)
        {
            ServersFile data    = LoadServers();
            int         removed = data.Servers.RemoveAll(s => s.Id == id);
            if (removed == 0) return false;
            Save(_serversPath, data);
            return true;
        }
    }

    public void SetServerCurrentModel(Guid serverId, string? modelName)
    {
        lock (_serversLock)
        {
            ServersFile data   = LoadServers();
            Server?     server = data.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server is null) return;
            server.CurrentModelName = modelName;
            Save(_serversPath, data);
        }
    }

    // ── Models ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<Model> GetModels()
    {
        lock (_modelsLock) return LoadModels().Models;
    }

    public Model? GetModel(string name)
    {
        lock (_modelsLock) return LoadModels().Models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public Model AddModel(Model model)
    {
        lock (_modelsLock)
        {
            ModelsFile data = LoadModels();
            data.Models.Add(model);
            Save(_modelsPath, data);
            return model;
        }
    }

    public bool UpdateModel(Model updated)
    {
        lock (_modelsLock)
        {
            ModelsFile data = LoadModels();
            int idx = data.Models.FindIndex(m => m.Name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            data.Models[idx] = updated;
            Save(_modelsPath, data);
            return true;
        }
    }

    public bool RemoveModel(string name)
    {
        lock (_modelsLock)
        {
            ModelsFile data    = LoadModels();
            int        removed = data.Models.RemoveAll(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(_modelsPath, data);
            return true;
        }
    }

    // ── Model notes ─────────────────────────────────────────────────────────────

    public Dictionary<string, string> GetAllNotes()
    {
        lock (_modelsLock) return new Dictionary<string, string>(LoadModels().ModelNotes);
    }

    public void SetNote(string modelName, string notes)
    {
        lock (_modelsLock)
        {
            ModelsFile data = LoadModels();
            if (string.IsNullOrWhiteSpace(notes))
                data.ModelNotes.Remove(modelName);
            else
                data.ModelNotes[modelName] = notes;
            Save(_modelsPath, data);
        }
    }

    // ── Agents ────────────────────────────────────────────────────────────────────

    public IReadOnlyList<AgentDefinition> GetAgents()
    {
        lock (_agentsLock) return LoadAgents().Agents;
    }

    public SharedPromptsFile GetSharedPrompts()
    {
        lock (_agentsLock) return LoadAgents().Shared ?? new SharedPromptsFile();
    }

    public void UpdateSharedPrompts(SharedPromptsFile updated) =>
        MergeShared(JsonSerializer.SerializeToNode(updated, JsonOpts)!.AsObject());

    /// <summary>The Shared section exactly as it is in the file (Budgets and ToolSystem included, which the typed
    /// model does not carry), with top-level keys in camelCase for the control panel.</summary>
    public JsonObject GetSharedForUi()
    {
        lock (_agentsLock)
        {
            JsonObject result = new();
            if (LoadAgentsRaw()["Shared"] is JsonObject shared)
                foreach ((string k, JsonNode? v) in shared)
                    result[char.ToLowerInvariant(k[0]) + k[1..]] = v?.DeepClone();
            return result;
        }
    }

    public AgentDefinition? GetAgent(string name)
    {
        lock (_agentsLock) return LoadAgents().Agents
            .FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool UpdateAgent(AgentDefinition updated) =>
        MergeAgent(updated.Name, JsonSerializer.SerializeToNode(updated, JsonOpts)!.AsObject());

    // ── Non-destructive edits ───────────────────────────────────────────────────
    // Agents.json carries more than AgentDefinition models (sampler values, Priority, PreloadedTools, budgets,
    // Shared.Budgets/ToolSystem …). Rewriting it from the typed model silently deleted all of that, so edits are
    // merged into the file's own JSON instead and everything they don't mention is left exactly as it was.

    private static readonly JsonDocumentOptions DocOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private JsonObject LoadAgentsRaw() =>
        File.Exists(_agentsPath) ? JsonNode.Parse(File.ReadAllText(_agentsPath), documentOptions: DocOpts)?.AsObject() ?? new() : new();

    private static string? KeyFor(JsonObject o, string name) =>
        o.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsDefault(JsonNode? v) => v switch
    {
        null => true,
        JsonObject o => o.Count == 0,
        JsonArray a => a.Count == 0,
        JsonValue jv => (jv.TryGetValue(out bool b) && !b) || (jv.TryGetValue(out int i) && i == 0) || (jv.TryGetValue(out string? s) && string.IsNullOrEmpty(s)),
        _ => false,
    };

    /// <summary>Objects merge key by key (names matched case-insensitively); anything else replaces. A key the file
    /// does not have yet is only added when it carries a real value, so saving never litters it with defaults.</summary>
    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach ((string k, JsonNode? v) in patch)
        {
            string? existing = KeyFor(target, k);
            if (existing is null)
            {
                if (!IsDefault(v)) target[char.ToUpperInvariant(k[0]) + k[1..]] = v?.DeepClone();
                continue;
            }
            if (v is JsonObject po && target[existing] is JsonObject to) MergeInto(to, po);
            else target[existing] = v?.DeepClone();
        }
    }

    /// <summary>Applies the given fields to one agent in Agents.json. The agent's name is never changed.</summary>
    public bool MergeAgent(string name, JsonObject patch)
    {
        lock (_agentsLock)
        {
            JsonObject root = LoadAgentsRaw();
            if (root[KeyFor(root, "Agents") ?? "Agents"] is not JsonArray agents) return false;
            JsonObject? target = agents.OfType<JsonObject>().FirstOrDefault(a =>
                string.Equals(a[KeyFor(a, "Name") ?? "Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (target is null) return false;

            JsonObject copy = (JsonObject)patch.DeepClone();
            if (KeyFor(copy, "Name") is { } nameKey) copy.Remove(nameKey);
            MergeInto(target, copy);
            File.WriteAllText(_agentsPath, root.ToJsonString(JsonOpts));
            return true;
        }
    }

    /// <summary>Applies the given fields to the Shared section of Agents.json (creating it if missing).</summary>
    public void MergeShared(JsonObject patch)
    {
        lock (_agentsLock)
        {
            JsonObject root = LoadAgentsRaw();
            string key = KeyFor(root, "Shared") ?? "Shared";
            if (root[key] is not JsonObject shared) root[key] = shared = new JsonObject();
            MergeInto(shared, (JsonObject)patch.DeepClone());
            File.WriteAllText(_agentsPath, root.ToJsonString(JsonOpts));
        }
    }

    /// <summary>Adds a control-panel-made agent to Agents.json (merged into the raw file, so nothing else in it is touched).
    /// False if the name is taken.</summary>
    public bool CreateCustomAgent(string name, string serverName, string? slotName, string prompt)
    {
        lock (_agentsLock)
        {
            JsonObject root = LoadAgentsRaw();
            string key = KeyFor(root, "Agents") ?? "Agents";
            if (root[key] is not JsonArray agents) root[key] = agents = new JsonArray();
            if (agents.OfType<JsonObject>().Any(a => string.Equals(a[KeyFor(a, "Name") ?? "Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
                return false;
            JsonObject agent = new() { ["Name"] = name, ["Custom"] = true, ["Enabled"] = true, ["ServerName"] = serverName, ["AgentPrompt"] = prompt };
            if (!string.IsNullOrEmpty(slotName)) agent["SlotName"] = slotName;
            agents.Add(agent);
            File.WriteAllText(_agentsPath, root.ToJsonString(JsonOpts));
            return true;
        }
    }

    /// <summary>One agent's Agents.json entry as JSON text, or null if there is no such agent.</summary>
    public string? GetAgentEntryJson(string name)
    {
        lock (_agentsLock)
        {
            JsonObject root = LoadAgentsRaw();
            if (root[KeyFor(root, "Agents") ?? "Agents"] is not JsonArray agents) return null;
            return agents.OfType<JsonObject>().FirstOrDefault(a =>
                string.Equals(a[KeyFor(a, "Name") ?? "Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))?.ToJsonString();
        }
    }

    /// <summary>Deletes a control-panel-made agent. Shipped agents are never removed by this.</summary>
    public bool RemoveCustomAgent(string name)
    {
        lock (_agentsLock)
        {
            JsonObject root = LoadAgentsRaw();
            if (root[KeyFor(root, "Agents") ?? "Agents"] is not JsonArray agents) return false;
            JsonObject? target = agents.OfType<JsonObject>().FirstOrDefault(a =>
                string.Equals(a[KeyFor(a, "Name") ?? "Name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)
                && a[KeyFor(a, "Custom") ?? "Custom"]?.GetValue<bool>() == true);
            if (target is null) return false;
            agents.Remove(target);
            File.WriteAllText(_agentsPath, root.ToJsonString(JsonOpts));
            return true;
        }
    }

    // ── Pipelines ──────────────────────────────────────────────────────────

    private string PipelinesPath => Path.Combine(_dir, "Pipelines.json");
    private readonly object _pipelinesLock = new();

    /// <summary>Pipelines.json as written, or null if the user has never saved one.</summary>
    public JsonObject? GetPipelinesRaw()
    {
        lock (_pipelinesLock)
            return File.Exists(PipelinesPath) ? JsonNode.Parse(File.ReadAllText(PipelinesPath), documentOptions: DocOpts)?.AsObject() : null;
    }

    public void SavePipelinesRaw(JsonObject pipelines)
    {
        lock (_pipelinesLock) File.WriteAllText(PipelinesPath, pipelines.ToJsonString(JsonOpts));
    }

    public AgentDefinition AddAgent(AgentDefinition agent)
    {
        lock (_agentsLock)
        {
            AgentsFile data = LoadAgents();
            data.Agents.Add(agent);
            Save(_agentsPath, data);
            return agent;
        }
    }

    public bool RemoveAgent(string name)
    {
        lock (_agentsLock)
        {
            AgentsFile data    = LoadAgents();
            int        removed = data.Agents.RemoveAll(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(_agentsPath, data);
            return true;
        }
    }

    public void RenameServerInAgents(string oldName, string newName)
    {
        lock (_agentsLock)
        {
            AgentsFile data    = LoadAgents();
            bool       changed = false;
            foreach (AgentDefinition a in data.Agents)
            {
                if (a.ServerName?.Equals(oldName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    a.ServerName = newName;
                    changed      = true;
                }
            }
            if (changed) Save(_agentsPath, data);
        }
    }

    // ── Voice ───────────────────────────────────────────────────────────────────

    public string? GetDefaultVoiceModel(string engine)
    {
        lock (_voiceLock)
        {
            Dictionary<string, string> d = LoadVoice().DefaultModels;
            return d.TryGetValue(engine, out string m) && !string.IsNullOrWhiteSpace(m) ? m : null;
        }
    }

    public void SetDefaultVoiceModel(string engine, string? modelName)
    {
        lock (_voiceLock)
        {
            VoiceFile data = LoadVoice();
            if (string.IsNullOrWhiteSpace(modelName))
                data.DefaultModels.Remove(engine);
            else
                data.DefaultModels[engine] = modelName;
            Save(_voicePath, data);
        }
    }

    // ── IO ───────────────────────────────────────────────────────────────────────

    private ServersFile LoadServers() => Load<ServersFile>(_serversPath);
    private ModelsFile  LoadModels()  => Load<ModelsFile>(_modelsPath);
    private AgentsFile  LoadAgents()  => Load<AgentsFile>(_agentsPath);
    private VoiceFile   LoadVoice()   => Load<VoiceFile>(_voicePath);

    private T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts) ?? new T(); }
        catch { return new T(); }
    }

    private void Save<T>(string path, T data)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts));
    }
}

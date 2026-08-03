using System.Text.Json;
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
    public string  SystemPrompt      { get; set; } = "";
    public Dictionary<string, string>? PromptTemplates { get; set; }
    // Modelled here or Save() drops them: the Coder's per-phase prompts, and Curiosity's rulebook opt-out.
    public Dictionary<string, PhaseConfig>? Phases { get; set; }
    public bool?   UseGraphRulebook  { get; set; }
    public bool    Enabled           { get; set; } = true;
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
        public string? DefaultModelName { get; set; }
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

    /// <summary>
    /// If Agents.json is missing or empty, bootstrap it from the bundled dev-side Agents.json.
    /// Called by ARI.cs after PersistentData is constructed, passing the exe-dir fallback path.
    /// </summary>
    public void EnsureAgentsFileFromFallback(string fallbackPath)
    {
        lock (_agentsLock)
        {
            AgentsFile existing = LoadAgents();
            if (existing.Agents.Count > 0) return;

            // Every path out of here used to be silent, so a failed seed looked identical to a working
            // one until the Agents tab came up empty. It is load-bearing now: say what went wrong.
            if (!File.Exists(fallbackPath))
            {
                Shared.Logger.LogError("[PersistentData] No Agents.json in app data and no bundled default at {Path} — ARI will start with no agents.", fallbackPath);
                return;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fallbackPath),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (!doc.RootElement.TryGetProperty("Agents", out JsonElement arr))
                {
                    Shared.Logger.LogError("[PersistentData] Bundled Agents.json at {Path} has no 'Agents' array — cannot seed.", fallbackPath);
                    return;
                }

                var agents = arr.EnumerateArray()
                    .Select(el => JsonSerializer.Deserialize<AgentDefinition>(el.GetRawText(), JsonOpts))
                    .Where(a => a is not null)
                    .Select(a => a!)
                    .ToList();

                if (agents.Count == 0)
                {
                    Shared.Logger.LogError("[PersistentData] Bundled Agents.json at {Path} contained no readable agents — cannot seed.", fallbackPath);
                    return;
                }

                Save(_agentsPath, new AgentsFile { Agents = agents });
                Shared.Logger.LogInformation("[PersistentData] Seeded {Count} agent(s) into {Path} from the bundled default.", agents.Count, _agentsPath);
            }
            catch (Exception ex)
            {
                Shared.Logger.LogError(ex, "[PersistentData] Could not seed Agents.json from {Path}.", fallbackPath);
            }
        }
    }

    /// <summary>
    /// Seeds Servers.json and Models.json from the bundled defaults on first run — the demo server and
    /// the one model it expects. Same rule as the agents: only when the user has none, and never again,
    /// so a machine's own servers are not touched by an update.
    /// </summary>
    public void EnsureServersAndModelsFromFallback(string serversFallback, string modelsFallback)
    {
        lock (_serversLock)
        {
            if (LoadServers().Servers.Count == 0)
                SeedFile(serversFallback, _serversPath, "Servers");
        }
        lock (_modelsLock)
        {
            if (LoadModels().Models.Count == 0)
                SeedFile(modelsFallback, _modelsPath, "Models");
        }
    }

    // Copies a bundled default verbatim. Verbatim matters: it keeps the shipped file the single source
    // of the demo config, rather than a shape this class has to re-describe in C#.
    private static void SeedFile(string fallbackPath, string destPath, string label)
    {
        if (!File.Exists(fallbackPath))
        {
            Shared.Logger.LogError("[PersistentData] No {Label}.json in app data and no bundled default at {Path}.", label, fallbackPath);
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(fallbackPath, destPath, overwrite: false);
            Shared.Logger.LogInformation("[PersistentData] Seeded {Label}.json from the bundled default.", label);
        }
        catch (Exception ex)
        {
            Shared.Logger.LogError(ex, "[PersistentData] Could not seed {Label}.json from {Path}.", label, fallbackPath);
        }
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

    public void UpdateSharedPrompts(SharedPromptsFile updated)
    {
        lock (_agentsLock)
        {
            AgentsFile data = LoadAgents();
            data.Shared = updated;
            Save(_agentsPath, data);
        }
    }

    public AgentDefinition? GetAgent(string name)
    {
        lock (_agentsLock) return LoadAgents().Agents
            .FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool UpdateAgent(AgentDefinition updated)
    {
        lock (_agentsLock)
        {
            AgentsFile data = LoadAgents();
            int idx = data.Agents.FindIndex(a => a.Name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            data.Agents[idx] = updated;
            Save(_agentsPath, data);
            return true;
        }
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

    public string? GetDefaultVoiceModel()
    {
        lock (_voiceLock) return LoadVoice().DefaultModelName;
    }

    public void SetDefaultVoiceModel(string? modelName)
    {
        lock (_voiceLock)
        {
            VoiceFile data = LoadVoice();
            data.DefaultModelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName;
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

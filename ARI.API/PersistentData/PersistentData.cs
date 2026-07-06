using System.Text.Json;
using ARI.LLM;

namespace ARI.API.Data;

public sealed class AgentDefinition
{
    public string  Name              { get; set; } = "";
    public string  ServerName        { get; set; } = "";
    public string  SystemPrompt      { get; set; } = "";
    public bool    Enabled           { get; set; } = true;
    public int?    Slot              { get; set; }
    public bool    Think             { get; set; }
    public int     ThinkingBudget    { get; set; }
    public int     MaxTokens         { get; set; } = -1;
    public int     MaxToolCalls      { get; set; }
    public int     MaxContextTokens  { get; set; }
    public bool    NativeTools       { get; set; }
    public double? Temperature       { get; set; }
    public double? TopP              { get; set; }
    public int?    TopK              { get; set; }
    public double? RepeatPenalty     { get; set; }
    public double? PresencePenalty   { get; set; }
    public double? FrequencyPenalty  { get; set; }
    // Dialogue-specific
    public int?    ShortTermMemoryLimit { get; set; }
    // Engram-specific
    public int?    SweepIntervalMinutes { get; set; }
    public int?    RecursiveBrainSearchDepth { get; set; }
    // Memory-specific (also used by Engram)
    public int?    MinP              { get; set; }
}

/// <summary>
/// Persists LLM data across three files under ~/.ari/PersistentData/:
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
        string ariDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari", "Server");
        _dir = Path.Combine(ariDir, "PersistentData");
        Directory.CreateDirectory(_dir);

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

            if (!File.Exists(fallbackPath)) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(fallbackPath),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                if (!doc.RootElement.TryGetProperty("Agents", out JsonElement arr)) return;

                var agents = arr.EnumerateArray()
                    .Select(el => JsonSerializer.Deserialize<AgentDefinition>(el.GetRawText(), JsonOpts))
                    .Where(a => a is not null)
                    .Select(a => a!)
                    .ToList();

                if (agents.Count > 0)
                    Save(_agentsPath, new AgentsFile { Agents = agents });
            }
            catch { }
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

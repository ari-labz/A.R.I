using System.Text.Json;
using ARI.LLM;

namespace ARI.API.Data;

public sealed record AgentAssignment(string ServerName, int? Slot);

/// <summary>
/// Persists LLM servers, model records, and model notes to ~/.ari/llm-data.json.
/// All mutations go through this class. Thread-safe.
/// </summary>
public class PersistentData
{

    private sealed class DataFile
    {
        public List<Server>                      Servers           { get; set; } = new();
        public List<Model>                       Models            { get; set; } = new();
        public Dictionary<string, string>           ModelNotes        { get; set; } = new();
        public Dictionary<string, AgentAssignment>  AgentAssignments  { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented           = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _lock = new();

    public PersistentData()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ari");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "llm-data.json");
    }

    // ── Servers ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<Server> GetServers()
    {
        lock (_lock) return Load().Servers;
    }

    public Server? GetServer(Guid id)
    {
        lock (_lock) return Load().Servers.FirstOrDefault(s => s.Id == id);
    }

    public Server? GetServerByName(string name)
    {
        lock (_lock) return Load().Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public Server AddServer(Server server)
    {
        lock (_lock)
        {
            DataFile data = Load();
            data.Servers.Add(server);
            Save(data);
            return server;
        }
    }

    public bool UpdateServer(Server updated)
    {
        lock (_lock)
        {
            DataFile data = Load();
            int idx = data.Servers.FindIndex(s => s.Id == updated.Id);
            if (idx < 0) return false;
            data.Servers[idx] = updated;
            Save(data);
            return true;
        }
    }

    public bool RemoveServer(Guid id)
    {
        lock (_lock)
        {
            DataFile data    = Load();
            int      removed = data.Servers.RemoveAll(s => s.Id == id);
            if (removed == 0) return false;
            Save(data);
            return true;
        }
    }

    public void SetServerCurrentModel(Guid serverId, string? modelName)
    {
        lock (_lock)
        {
            DataFile  data   = Load();
            Server? server = data.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server is null) return;
            server.CurrentModelName = modelName;
            Save(data);
        }
    }

    // ── Models ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<Model> GetModels()
    {
        lock (_lock) return Load().Models;
    }

    public Model? GetModel(string name)
    {
        lock (_lock) return Load().Models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public Model AddModel(Model model)
    {
        lock (_lock)
        {
            DataFile data = Load();
            data.Models.Add(model);
            Save(data);
            return model;
        }
    }

    public bool UpdateModel(Model updated)
    {
        lock (_lock)
        {
            DataFile data = Load();
            int idx = data.Models.FindIndex(m => m.Name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            data.Models[idx] = updated;
            Save(data);
            return true;
        }
    }

    public bool RemoveModel(string name)
    {
        lock (_lock)
        {
            DataFile data    = Load();
            int      removed = data.Models.RemoveAll(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            Save(data);
            return true;
        }
    }

    // ── Model notes (replaces ModelNotesStore) ──────────────────────────────────

    public Dictionary<string, string> GetAllNotes()
    {
        lock (_lock) return new Dictionary<string, string>(Load().ModelNotes);
    }

    public void SetNote(string modelName, string notes)
    {
        lock (_lock)
        {
            DataFile data = Load();
            if (string.IsNullOrWhiteSpace(notes))
                data.ModelNotes.Remove(modelName);
            else
                data.ModelNotes[modelName] = notes;
            Save(data);
        }
    }

    // ── Agent assignments ────────────────────────────────────────────────────────

    public Dictionary<string, AgentAssignment> GetAgentAssignments()
    {
        lock (_lock) return new Dictionary<string, AgentAssignment>(Load().AgentAssignments);
    }

    public AgentAssignment? GetAgentAssignment(string agentName)
    {
        lock (_lock) return Load().AgentAssignments.GetValueOrDefault(agentName);
    }

    public void SetAgentAssignment(string agentName, AgentAssignment assignment)
    {
        lock (_lock)
        {
            DataFile data = Load();
            data.AgentAssignments[agentName] = assignment;
            Save(data);
        }
    }

    public void ClearAgentAssignment(string agentName)
    {
        lock (_lock)
        {
            DataFile data = Load();
            data.AgentAssignments.Remove(agentName);
            Save(data);
        }
    }

    // ── Seed ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Imports servers from Servers.json on first boot. Skipped if servers already exist in persistent data.
    /// </summary>
    public void SeedFromFile(string serversJsonPath)
    {
        lock (_lock)
        {
            DataFile data = Load();
            if (data.Servers.Count > 0) return;
            if (!File.Exists(serversJsonPath)) return;

            DataFile seed = JsonSerializer.Deserialize<DataFile>(File.ReadAllText(serversJsonPath), JsonOpts) ?? new();
            foreach (Server s in seed.Servers)
                data.Servers.Add(s);

            Save(data);
        }
    }

    // ── IO ───────────────────────────────────────────────────────────────────────

    private DataFile Load()
    {
        if (!File.Exists(_path)) return new DataFile();

        try
        {
            return JsonSerializer.Deserialize<DataFile>(File.ReadAllText(_path), JsonOpts) ?? new DataFile();
        }
        catch { return new DataFile(); }
    }

    private void Save(DataFile data)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(data, JsonOpts));
    }
}

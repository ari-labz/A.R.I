using ARI.API;
using Microsoft.Extensions.Logging;

namespace ARI.Core.Scripts;

public class ModelManager : IDisposable
{
    private readonly AriLLMConfig       llmConfig;
    private readonly string             modelsPath;
    private readonly ModelManagerHolder holder;
    private readonly ModelSettingsStore settingsStore;
    private readonly ILogger            logger;

    private readonly Dictionary<string, (LocalLlamaServer Server, string ActiveFile)> servers = new();
    private readonly SemaphoreSlim switchLock = new(1, 1);

    public ModelManager(
        AriLLMConfig       llmConfig,
        string             executableDirectory,
        ModelManagerHolder holder,
        ILoggerFactory     loggerFactory)
    {
        this.llmConfig    = llmConfig;
        this.holder       = holder;
        this.settingsStore = new ModelSettingsStore();
        logger            = loggerFactory.CreateLogger<ModelManager>();

        modelsPath = Path.IsPathRooted(llmConfig.ModelsPath)
            ? llmConfig.ModelsPath
            : Path.GetFullPath(Path.Combine(executableDirectory, llmConfig.ModelsPath));
    }

    public async Task StartAllServersAsync()
    {
        holder.RegisterSwitchDelegate((serverName, file) => BeginSwitch(serverName, file));
        PublishModelList();

        List<Task> boots = new();
        foreach (LlamaServerConfig serverConfig in llmConfig.Servers)
            boots.Add(BootServer(serverConfig));

        await Task.WhenAll(boots);
        PublishModelList();
    }

    private async Task BootServer(LlamaServerConfig serverConfig)
    {
        string? targetFile = ResolveStartupFile(serverConfig);
        if (targetFile is null)
        {
            logger.LogWarning("[ModelManager] No model file found for server '{Server}' — skipping.", serverConfig.Name);
            return;
        }

        LlamaModelConfig modelCfg = BuildConfig(targetFile);
        LocalLlamaServer server   = new(serverConfig, modelCfg, AppContext.BaseDirectory);
        await server.IsReady();

        servers[serverConfig.Name] = (server, targetFile);
        holder.SetServerModel(serverConfig.Name, targetFile, modelCfg.EffectiveName, server.Pid);
        logger.LogInformation("[ModelManager] Server '{Server}' ready — {Model} (PID {Pid}).",
            serverConfig.Name, modelCfg.EffectiveName, server.Pid);
    }

    public ModelSwitchJob BeginSwitch(string serverName, string relativeFile)
    {
        ModelSwitchJob job = holder.BeginSwitchJob(serverName, relativeFile);
        _ = Task.Run(() => RunSwitch(job, serverName, relativeFile));
        return job;
    }

    private async Task RunSwitch(ModelSwitchJob job, string serverName, string relativeFile)
    {
        await switchLock.WaitAsync();
        try
        {
            LlamaServerConfig? serverConfig = llmConfig.Servers.FirstOrDefault(s => s.Name == serverName);
            if (serverConfig is null)
            {
                job.AddEvent("error", $"Unknown server '{serverName}'.", 0);
                job.Complete(false, $"Unknown server '{serverName}'.");
                return;
            }

            if (servers.TryGetValue(serverName, out var current))
            {
                string currentName = Path.GetFileNameWithoutExtension(current.ActiveFile);
                job.AddEvent("idle-wait", $"Waiting for {currentName} to finish active requests…", 10);
                await WaitForIdle(serverConfig.Endpoint);

                job.AddEvent("powering-down", $"Powering down {currentName}…", 30);
                current.Server.Stop();
                current.Server.Dispose();
                servers.Remove(serverName);
                holder.SetServerModel(serverName, null, null, -1);
                job.AddEvent("powering-down", "Server stopped.", 50);
            }

            LlamaModelConfig modelCfg = BuildConfig(relativeFile);
            string           name     = modelCfg.EffectiveName;

            job.AddEvent("powering-up", $"Powering up {name}…", 60);
            LocalLlamaServer newServer = new(serverConfig, modelCfg, AppContext.BaseDirectory);
            await newServer.IsReady();

            servers[serverName] = (newServer, relativeFile);
            holder.SetServerModel(serverName, relativeFile, name, newServer.Pid);
            settingsStore.SetStartupFile(serverName, relativeFile);
            PublishModelList();

            job.AddEvent("ready", $"{name} is ready.", 100);
            job.Complete(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ModelManager] Switch on '{Server}' to {File} failed", serverName, relativeFile);
            job.AddEvent("error", ex.Message, 0);
            job.Complete(false, ex.Message);
        }
        finally
        {
            switchLock.Release();
        }
    }

    private void PublishModelList()
    {
        Directory.CreateDirectory(modelsPath);

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos = new List<ModelInfo>();

        string[] onDisk = Directory.GetFiles(modelsPath, "*.gguf", SearchOption.AllDirectories);
        foreach (string fullPath in onDisk.OrderBy(p => p))
        {
            string relFile = Path.GetRelativePath(modelsPath, fullPath).Replace('\\', '/');
            if (relFile.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;

            string normRel = NormFile(relFile);
            if (!seen.Add(normRel)) continue;

            string name = Path.GetFileNameWithoutExtension(relFile);
            long   size = new FileInfo(fullPath).Length;

            (string downloadUrl, string mmprojRel, bool supportsVision, bool hasMtp) = ReadMeta(relFile);
            bool hasMmproj = !string.IsNullOrWhiteSpace(mmprojRel);

            infos.Add(new ModelInfo(name, relFile, size, true, false, hasMmproj, supportsVision, hasMtp, downloadUrl));
        }

        holder.Initialize(infos);
    }

    private (string DownloadUrl, string MmprojRel, bool SupportsVision, bool HasMtp) ReadMeta(string relFile)
    {
        string dir = Path.GetDirectoryName(Path.Combine(modelsPath, relFile)) ?? modelsPath;

        string urlFile    = Path.Combine(dir, "url.txt");
        string downloadUrl = File.Exists(urlFile) ? File.ReadAllText(urlFile).Trim() : "";

        bool supportsVision = File.Exists(Path.Combine(dir, "mmproj-url.txt"));

        string[] mmprojs  = Directory.GetFiles(dir, "mmproj-*.gguf");
        string   mmprojRel = mmprojs.Length > 0
            ? Path.GetRelativePath(modelsPath, mmprojs[0]).Replace('\\', '/')
            : "";
        if (!string.IsNullOrEmpty(mmprojRel)) supportsVision = true;

        bool hasMtp = downloadUrl.Contains("-MTP-", StringComparison.OrdinalIgnoreCase)
                   || relFile.Contains("MTP", StringComparison.OrdinalIgnoreCase);

        return (downloadUrl, mmprojRel, supportsVision, hasMtp);
    }

    private LlamaModelConfig BuildConfig(string relativeFile)
    {
        (string downloadUrl, string mmprojRel, _, bool hasMtp) = ReadMeta(relativeFile);
        return new LlamaModelConfig
        {
            File            = relativeFile,
            MmprojFile      = mmprojRel,
            UseMtp          = hasMtp,
            ModelsPath      = modelsPath,
            DownloadBaseUrl = downloadUrl,
        };
    }

    private string? ResolveStartupFile(LlamaServerConfig serverConfig)
    {
        // 1. User override stored in model-settings.json
        string stored = settingsStore.GetStartupFile(serverConfig.Name);
        if (!string.IsNullOrWhiteSpace(stored) && File.Exists(Path.Combine(modelsPath, stored)))
            return stored;

        // 2. Config StartupModel
        if (!string.IsNullOrWhiteSpace(serverConfig.StartupModel) &&
            File.Exists(Path.Combine(modelsPath, serverConfig.StartupModel)))
            return serverConfig.StartupModel;

        // 3. First .gguf on disk
        string[] all = Directory.GetFiles(modelsPath, "*.gguf", SearchOption.AllDirectories);
        return all.OrderBy(p => p)
                  .Select(p => Path.GetRelativePath(modelsPath, p).Replace('\\', '/'))
                  .FirstOrDefault(f => !f.Contains("mmproj", StringComparison.OrdinalIgnoreCase));
    }

    private async Task WaitForIdle(string endpoint, int timeoutSeconds = 60)
    {
        using HttpClient hc = new();
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                HttpResponseMessage resp = await hc.GetAsync($"{endpoint}/slots");
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
                    bool allIdle = true;
                    foreach (System.Text.Json.JsonElement slot in doc.RootElement.EnumerateArray())
                    {
                        if (slot.GetProperty("state").GetInt32() != 0) { allIdle = false; break; }
                    }
                    if (allIdle) return;
                }
            }
            catch { return; }
            await Task.Delay(500);
        }
        logger.LogWarning("[ModelManager] Timed out waiting for idle on '{Endpoint}' — forcing shutdown.", endpoint);
    }

    private static string NormFile(string f) => f.Replace('\\', '/').TrimStart('/');

    public void Dispose()
    {
        foreach (var (server, _) in servers.Values)
            server.Dispose();
        switchLock.Dispose();
    }
}

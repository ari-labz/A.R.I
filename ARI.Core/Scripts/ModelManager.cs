using ARI.API;
using Microsoft.Extensions.Logging;

namespace ARI.Core.Scripts;

public class ModelManager : IDisposable
{
    private readonly AriLLMConfig       llmConfig;
    private readonly string             modelsPath;
    private readonly ModelManagerHolder holder;
    private readonly ILogger            logger;

    private LocalLlamaServer?  activeServer;
    private string?            activeFile;

    private readonly SemaphoreSlim switchLock = new(1, 1);

    public ModelManager(
        AriLLMConfig       llmConfig,
        string             executableDirectory,
        ModelManagerHolder holder,
        ILoggerFactory     loggerFactory)
    {
        this.llmConfig = llmConfig;
        this.holder    = holder;
        logger         = loggerFactory.CreateLogger<ModelManager>();

        modelsPath = Path.IsPathRooted(llmConfig.ModelsPath)
            ? llmConfig.ModelsPath
            : Path.GetFullPath(Path.Combine(executableDirectory, llmConfig.ModelsPath));
    }

    public async Task StartInitialModelAsync(string? preferredFile = null)
    {
        holder.RegisterSwitchDelegate(file => BeginSwitch(file));
        PublishModelList(preferredFile, null);

        // Resolve which file to launch: user preference → config default → first on disk
        string? targetFile = ResolveStartupFile(preferredFile);
        if (targetFile is null) return;

        LlamaModelConfig cfg = BuildConfig(targetFile);
        activeFile   = targetFile;
        activeServer = new LocalLlamaServer(GetServer(), cfg, AppContext.BaseDirectory);
        await activeServer.IsReady();

        holder.SetActiveModel(targetFile, cfg.EffectiveName, activeServer.Pid);
        PublishModelList(preferredFile, targetFile);
    }

    public ModelSwitchJob BeginSwitch(string relativeFile)
    {
        ModelSwitchJob job = holder.BeginSwitchJob(relativeFile);
        _ = Task.Run(() => RunSwitch(job, relativeFile));
        return job;
    }

    private async Task RunSwitch(ModelSwitchJob job, string relativeFile)
    {
        await switchLock.WaitAsync();
        try
        {
            if (activeServer is not null)
            {
                string currentName = activeFile is not null
                    ? Path.GetFileNameWithoutExtension(activeFile)
                    : "current model";

                job.AddEvent("idle-wait", $"Waiting for {currentName} to finish active requests…", 10);
                await WaitForIdle(GetServer().Endpoint);

                job.AddEvent("powering-down", $"Powering down {currentName}…", 30);
                activeServer.Stop();
                activeServer.Dispose();
                activeServer = null;
                activeFile   = null;
                job.AddEvent("powering-down", "Server stopped.", 50);
                holder.SetActiveModel(null, null, -1);
            }

            LlamaModelConfig cfg    = BuildConfig(relativeFile);
            string           name   = cfg.EffectiveName;
            LlamaServerConfig server = GetServer();

            job.AddEvent("powering-up", $"Powering up {name}…", 60);
            LocalLlamaServer newServer = new(server, cfg, AppContext.BaseDirectory);
            await newServer.IsReady();

            activeServer = newServer;
            activeFile   = relativeFile;
            holder.SetActiveModel(relativeFile, name, newServer.Pid);
            PublishModelList(null, relativeFile);

            job.AddEvent("ready", $"{name} is ready.", 100);
            job.Complete(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ModelManager] Switch to {File} failed", relativeFile);
            job.AddEvent("error", ex.Message, 0);
            job.Complete(false, ex.Message);
        }
        finally
        {
            switchLock.Release();
        }
    }

    private void PublishModelList(string? startupFile, string? activeFileOverride)
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

            string name    = Path.GetFileNameWithoutExtension(relFile);
            long   size    = new FileInfo(fullPath).Length;
            bool   isStart = !string.IsNullOrWhiteSpace(startupFile) &&
                             NormFile(startupFile).Equals(normRel, StringComparison.OrdinalIgnoreCase);

            (string downloadUrl, string mmprojRel, bool supportsVision, bool hasMtp) = ReadMeta(relFile);
            bool hasMmproj = !string.IsNullOrWhiteSpace(mmprojRel);

            infos.Add(new ModelInfo(name, relFile, size, true, isStart, hasMmproj, supportsVision, hasMtp, downloadUrl));
        }

        holder.Initialize(infos);
    }

    // Reads metadata for a model from the companion files in its directory.
    private (string DownloadUrl, string MmprojRel, bool SupportsVision, bool HasMtp) ReadMeta(string relFile)
    {
        string dir = Path.GetDirectoryName(Path.Combine(modelsPath, relFile)) ?? modelsPath;

        // url.txt in the same folder as the .gguf
        string urlFile    = Path.Combine(dir, "url.txt");
        string downloadUrl = File.Exists(urlFile) ? File.ReadAllText(urlFile).Trim() : "";

        // mmproj-url.txt marks vision-capable even if the mmproj isn't downloaded yet
        bool supportsVision = File.Exists(Path.Combine(dir, "mmproj-url.txt"));

        // Actual mmproj file on disk
        string[] mmprojs = Directory.GetFiles(dir, "mmproj-*.gguf");
        string   mmprojRel = mmprojs.Length > 0
            ? Path.GetRelativePath(modelsPath, mmprojs[0]).Replace('\\', '/')
            : "";
        if (!string.IsNullOrEmpty(mmprojRel)) supportsVision = true;

        // MTP: URL repo name or filename contains "MTP"
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

    private string? ResolveStartupFile(string? preferred)
    {
        // Try user-specified preferred file
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(Path.Combine(modelsPath, preferred)))
            return preferred;

        // Try config default
        if (!string.IsNullOrWhiteSpace(llmConfig.StartupModel) &&
            File.Exists(Path.Combine(modelsPath, llmConfig.StartupModel)))
            return llmConfig.StartupModel;

        // First .gguf on disk (skip mmproj)
        string[] all = Directory.GetFiles(modelsPath, "*.gguf", SearchOption.AllDirectories);
        return all.OrderBy(p => p)
                  .Select(p => Path.GetRelativePath(modelsPath, p).Replace('\\', '/'))
                  .FirstOrDefault(f => !f.Contains("mmproj", StringComparison.OrdinalIgnoreCase));
    }

    private LlamaServerConfig GetServer() =>
        llmConfig.Servers.Count > 0 ? llmConfig.Servers[0] : new LlamaServerConfig();

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
        logger.LogWarning("[ModelManager] Timed out waiting for idle — forcing shutdown.");
    }

    private static string NormFile(string f) => f.Replace('\\', '/').TrimStart('/');

    public void Dispose()
    {
        activeServer?.Dispose();
        switchLock.Dispose();
    }
}

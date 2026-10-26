using ARI.API.Data;
using ARI.Common;
using ARI.ImageGen;
using ARI.LLM;
using ARI.Voice;
using ARI.VoiceSynthesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ARI.API.Controllers;

// ── LLM Models & Servers API ──────────────────────────────────────────────────

[Route("api/v1/admin/models")]
[ApiController]
public class ModelsController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? llm => (LLMModule?)Modules.Llm;
    private string ModelsPath => llm?.ModelsPath ?? "";

    [HttpGet]
    public IActionResult GetModels()
    {
        Dictionary<string, string> notes      = persistentData.GetAllNotes();
        string mPath   = ModelsPath;

        HashSet<string?> activeModelNames  = llm?.Servers.Select(s => s.ActiveModel?.Name).Where(n => n is not null).ToHashSet() ?? [];
        HashSet<string?> startupModelNames = persistentData.GetServers().Select(s => s.CurrentModelName).Where(n => n is not null).ToHashSet();

        var models = persistentData.GetModels().Select(m =>
        {
            m.RefreshDownloadedState(mPath);
            return new
            {
                name               = m.Name,
                downloadLink       = m.DownloadLink,
                mmprojDownloadLink = m.MmprojDownloadLink,
                downloaded         = m.Downloaded,
                modelSize          = m.ModelSize,
                fileSizeMb         = m.FileSizeBytes > 0 ? Math.Round(m.FileSizeBytes / 1048576.0) : (double?)null,
                kvArch             = m.KvArch is { } a ? new { nLayers = a.NLayers, nKvHeads = a.NKvHeads, headDim = a.HeadDim } : null,
                moe                = m.MoE,
                mtp                = m.MTP,
                supportsThinking   = m.SupportsThinking,
                supportsReasoningEffort = m.SupportsReasoningEffort,
                notes              = notes.TryGetValue(m.Name, out string? n) ? n : "",
                active             = activeModelNames.Contains(m.Name),
                isStartup          = startupModelNames.Contains(m.Name),
            };
        }).ToList();

        var servers = persistentData.GetServers().Select(s =>
        {
            ServerStatus status = llm?.Servers.FirstOrDefault(r => r.Id == s.Id)?.Status ?? ServerStatus.Offline;
            Model?    active = llm?.Servers.FirstOrDefault(r => r.Id == s.Id)?.ActiveModel;
            DownloadProgress? dl = llm?.Servers.FirstOrDefault(r => r.Id == s.Id)?.Download;
            return new
            {
                id              = s.Id,
                startError      = llm?.Servers.FirstOrDefault(r => r.Id == s.Id)?.StartError,
                download        = dl is null ? null : new { file = dl.File, doneBytes = dl.DoneBytes, totalBytes = dl.TotalBytes, bytesPerSec = dl.BytesPerSec },
                name            = s.Name,
                status          = status.ToString(),
                activeModelName = active?.Name,
                endpoint        = s.FullEndpoint,
                port            = s.Port,
                contextSize     = s.ContextSize,
                parallelSlots   = s.ParallelSlots,
                slots           = s.Slots,
                kvCacheQuantK   = s.KvCacheQuantK,
                kvCacheQuantV   = s.KvCacheQuantV,

                currentModelName = s.CurrentModelName,
                autoStart       = s.BootStartup,
                unifiedCache    = s.UnifiedCache,

                // Sampler defaults — the control panel's server modal edits these directly.
                temperature      = s.Temperature,
                topP             = s.TopP,
                topK             = s.TopK,
                minP             = s.MinP,
                topNSigma        = s.TopNSigma,
                typicalP         = s.TypicalP,
                xtcProbability   = s.XtcProbability,
                xtcThreshold     = s.XtcThreshold,
                dynatempRange    = s.DynatempRange,
                dynatempExp      = s.DynatempExp,
                repeatLastN      = s.RepeatLastN,
                repeatPenalty    = s.RepeatPenalty,
                presencePenalty  = s.PresencePenalty,
                frequencyPenalty = s.FrequencyPenalty,
                dryMultiplier    = s.DryMultiplier,
                dryBase          = s.DryBase,
                dryAllowedLength = s.DryAllowedLength,
                dryPenaltyLastN  = s.DryPenaltyLastN,
                drySequenceBreakers = s.DrySequenceBreakers,
                mirostat         = s.Mirostat,
                mirostatLr       = s.MirostatLr,
                mirostatEnt      = s.MirostatEnt,
                seed             = s.Seed,
            };
        }).ToList();

        return Ok(new { models, servers });
    }

    // ── Model CRUD ────────────────────────────────────────────────────────────

    [HttpPost]
    public IActionResult AddModel([FromBody] Model model)
    {
        if (!SafeNames.IsSafe(model.Name))
            return BadRequest(new { error = "name is required and must be a plain name." });
        // A model file is found under the models folder, so its path may not be absolute or climb out of it.
        model.Path ??= ""; model.MmprojPath ??= "";
        if (Path.IsPathRooted(model.Path) || model.Path.Contains("..") || Path.IsPathRooted(model.MmprojPath) || model.MmprojPath.Contains(".."))
            return BadRequest(new { error = "path must stay inside the models folder." });

        persistentData.AddModel(model);
        model.RefreshDownloadedState(ModelsPath);
        return Ok(model);
    }

    /// <summary>Change a model, named by its name. Send only what changes; <c>notes</c> are the model's free-text notes,
    /// <c>supportsThinking</c> says whether it emits a reasoning chain (takes effect when its server next starts).</summary>
    [HttpPatch("{id}")]
    public IActionResult UpdateModel(string id, [FromBody] JsonObject changes)
    {
        Model? existing = persistentData.GetModel(id);
        if (existing is null) return NotFound(new { error = "Model not found." });

        JsonObject merged = JsonSerializer.SerializeToNode(existing)!.AsObject();
        foreach ((string key, JsonNode? value) in changes)
            if (key is not ("name" or "notes")) merged[key] = value?.DeepClone();
        Model updated = merged.Deserialize<Model>()!;
        if (Path.IsPathRooted(updated.Path) || updated.Path.Contains("..") || Path.IsPathRooted(updated.MmprojPath) || updated.MmprojPath.Contains(".."))
            return BadRequest(new { error = "path must stay inside the models folder." });

        persistentData.UpdateModel(updated);
        if (changes["notes"] is JsonNode notes) persistentData.SetNote(updated.Name, notes.GetValue<string?>() ?? "");
        updated.RefreshDownloadedState(ModelsPath);
        return Ok(updated);
    }

    private static readonly HttpClient HfClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The file list of a Hugging Face repo, fetched by the server rather than the browser. Model downloads
    /// already happen on this machine, so it is the one place that is known to reach Hugging Face — a browser behind a
    /// work proxy, an extension or a cross-site rule can't be relied on to.</summary>
    [HttpGet("hf-tree")]
    public async Task<IActionResult> HfTree([FromQuery] string? repo, CancellationToken ct)
    {
        if (repo is null || !Regex.IsMatch(repo, @"^[A-Za-z0-9][\w.-]*/[A-Za-z0-9][\w.-]*$"))
            return BadRequest(new { error = "That isn't a Hugging Face repo — expected owner/name." });
        try
        {
            using HttpResponseMessage r = await HfClient.GetAsync($"https://huggingface.co/api/models/{repo}/tree/main", ct);
            // Hugging Face answers 401, not 404, for a repo that doesn't exist (or is private and gated).
            if (r.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
                return NotFound(new { error = "Repo not found (or it's private)." });
            if (!r.IsSuccessStatusCode) return StatusCode(502, new { error = $"Hugging Face answered HTTP {(int)r.StatusCode}." });
            return Content(await r.Content.ReadAsStringAsync(ct), "application/json");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"The A·R·I server couldn't reach Hugging Face ({ex.Message})." });
        }
    }

    /// <summary>Removes a model from the registry. With <c>deleteFile=true</c> its files are deleted from disk too: the
    /// weights, the vision projector, and any leftover partial download. Nothing is touched when a running server is using
    /// the model, a file another model entry also points at is kept, and only files inside the models folder are ever
    /// deleted. The registry entry goes last, so a refusal or a failed delete leaves everything as it was.</summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteModel(string id, [FromQuery] bool deleteFile = false)
    {
        Model? model = persistentData.GetModel(id);
        if (model is null) return NotFound(new { error = "Model not found." });

        long freedBytes = 0;
        List<string> deleted = new(), kept = new();

        if (deleteFile)
        {
            foreach (Server running in llm?.Servers.Where(s => s.Status != ServerStatus.Offline
                && s.ActiveModel is not null && s.ActiveModel.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? new())
                running.Stop();

            string root = Path.GetFullPath(ModelsPath);
            List<Model> others = persistentData.GetModels().Where(o => !o.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            bool UsedByAnother(string rel) => others.Any(o =>
                string.Equals(o.Path, rel, StringComparison.OrdinalIgnoreCase) || string.Equals(o.MmprojPath, rel, StringComparison.OrdinalIgnoreCase));

            try
            {
                foreach (string rel in new[] { model.Path, model.MmprojPath }.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        kept.Add($"{rel} (outside the models folder)");
                        continue;
                    }
                    if (UsedByAnother(rel))
                    {
                        kept.Add($"{rel} (also used by another model)");
                        continue;
                    }
                    foreach (string file in new[] { full, full + ".tmp" })
                    {
                        if (!System.IO.File.Exists(file)) continue;
                        freedBytes += new FileInfo(file).Length;
                        System.IO.File.Delete(file);
                        deleted.Add(Path.GetRelativePath(root, file));
                    }
                    // A model kept in its own sub-folder leaves the folder behind once its files are gone.
                    string? dir = Path.GetDirectoryName(full);
                    if (dir is not null && dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return StatusCode(500, new { error = $"Couldn't delete the model's files: {ex.Message}. It was not removed from the list." });
            }
        }

        persistentData.RemoveModel(model.Name);
        return Ok(new { ok = true, freedBytes, deleted, kept });
    }

    /// <summary>The one global reasoning-effort dial (0/1/2 = low/medium/xhigh). Universal across all agents;
    /// scales each agent's thinking budget and drives the reasoning_effort wire field on supporting models.
    /// See Documentation/Server/ARI.LLM/Reasoning-Effort.</summary>
    [HttpGet("~/api/v1/reasoningEffort")]
    public IActionResult GetReasoningEffort() => Ok(new
    {
        step        = ReasoningEffortStore.Step,
        level       = ReasoningEffortStore.Level,
        levels      = ReasoningEffortStore.Levels,
        multipliers = ReasoningEffortStore.Multipliers,
        // Per-pipeline support: the composer shows the dial only when the model that will actually answer
        // supports reasoning_effort — Dialogue's model in Default mode, Coder's in Code mode.
        support     = new
        {
            dialogue = AgentModelSupportsEffort("Dialogue"),
            coder    = AgentModelSupportsEffort("Coder"),
        },
    });

    /// <summary>Whether the model bound to the named agent's server is online and supports reasoning_effort.</summary>
    private bool AgentModelSupportsEffort(string agentName)
    {
        if (llm is null || !llm.Agents.TryGetValue(agentName, out Agent? agent)) return false;
        Server? srv = llm.Servers.FirstOrDefault(s => s.Name == agent.ServerName);
        return srv?.ActiveModel?.SupportsReasoningEffort == true;
    }
}

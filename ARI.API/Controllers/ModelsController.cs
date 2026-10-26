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
            return new
            {
                id              = s.Id,
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

    [HttpDelete("{id}")]
    public IActionResult DeleteModel(string id)
    {
        if (!persistentData.RemoveModel(id))
            return NotFound(new { error = "Model not found." });
        return Ok(new { ok = true });
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

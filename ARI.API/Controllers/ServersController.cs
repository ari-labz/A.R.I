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

// ── Servers API ───────────────────────────────────────────────────────────────

[Route("api/v1/admin/servers")]
[ApiController]
public class ServersController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? llm => (LLMModule?)Modules.Llm;

    [HttpPost]
    public IActionResult AddServer([FromBody] Server server)
    {
        if (string.IsNullOrWhiteSpace(server.Name))
            return BadRequest(new { error = "name is required." });

        persistentData.AddServer(server);
        llm?.AddServer(server);
        return Ok(server);
    }

    /// <summary>Change a server. Send only what changes (<c>currentModelName</c> is the model it loads when it starts).</summary>
    [HttpPatch("{id:guid}")]
    public IActionResult UpdateServer(Guid id, [FromBody] JsonObject changes)
    {
        Server? existing = persistentData.GetServer(id);
        if (existing is null) return NotFound(new { error = "Server not found." });

        JsonObject merged = JsonSerializer.SerializeToNode(existing)!.AsObject();
        foreach ((string key, JsonNode? value) in changes)
            if (key != "id") merged[key] = value?.DeepClone();
        Server server = merged.Deserialize<Server>()!;
        if (string.IsNullOrWhiteSpace(server.Name)) return BadRequest(new { error = "name can't be empty." });

        persistentData.UpdateServer(server);
        llm?.UpdateServer(server);
        if (existing.Name != server.Name)
            persistentData.RenameServerInAgents(existing.Name, server.Name);

        return Ok(server);
    }

    [HttpDelete("{id:guid}")]
    public IActionResult DeleteServer(Guid id)
    {
        Server? live = llm?.Servers.FirstOrDefault(s => s.Id == id);
        live?.Stop();
        if (!persistentData.RemoveServer(id))
            return NotFound(new { error = "Server not found." });
        llm?.RemoveServer(id);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/start")]
    public IActionResult StartServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });

        Model? model = server.CurrentModelName is not null
            ? persistentData.GetModel(server.CurrentModelName)
            : null;
        if (model is null)
        {
            string reason = string.IsNullOrWhiteSpace(server.CurrentModelName)
                ? "has no model assigned"
                : $"points at '{server.CurrentModelName}', which isn't in the model list";
            return BadRequest(new { error = $"{server.Name} {reason}, so it can't start. Pick a model for it first." });
        }
        string modelsPath = llm!.ModelsPath;
        _ = Task.Run(() => server.StartAsync(model, modelsPath));
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/stop")]
    public IActionResult StopServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });
        server.Stop();
        return Ok(new { ok = true });
    }

    [HttpPost("{id:guid}/restart")]
    public IActionResult RestartServer(Guid id)
    {
        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null) return NotFound(new { error = "Server not found or LLM module unavailable." });
        _ = Task.Run(() => server.RestartAsync());
        return Ok(new { ok = true });
    }

    /// <summary>Load a different model onto a running server now (and remember it as the one to load next time).</summary>
    [HttpPost("{id:guid}/switch")]
    public IActionResult Switch(Guid id, [FromBody] SwitchModelRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
            return BadRequest(new { error = "modelName is required." });

        Server? server = llm?.Servers.FirstOrDefault(s => s.Id == id);
        if (server is null)
            return NotFound(new { error = "Server not found." });

        Model? model = persistentData.GetModel(req.ModelName);
        if (model is null)
            return NotFound(new { error = "Model not found." });

        string modelsPath = llm!.ModelsPath;
        _ = Task.Run(async () =>
        {
            await server.ChangeModelAsync(model, modelsPath);
            persistentData.SetServerCurrentModel(server.Id, model.Name);
        });

        return Ok(new { ok = true });
    }
}

public record SwitchModelRequest(string ModelName);

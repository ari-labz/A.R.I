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

// ── Agents API ────────────────────────────────────────────────────────────────

[Route("api/v1/admin/agents")]
[ApiController]
public class AgentsController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    [HttpGet]
    public IActionResult GetAgents()
    {
        IReadOnlyList<AgentDefinition> agents = persistentData.GetAgents();
        JsonObject shared = persistentData.GetSharedForUi();
        var servers = persistentData.GetServers().Select(s => new { name = s.Name, slots = s.Slots });
        return Ok(new { agents, shared, servers });
    }

    /// <summary>Names that belong to shipped agents, so a custom agent can never shadow one.</summary>
    private static readonly string[] ReservedNames = ["Dialogue", "Coder", "Context", "Awareness", "Memory", "Engram", "Refactor", "Curiosity", "Subagent", "Dreamer", "Shared"];

    public sealed record NewAgent(string Name, string? ServerName, string? SlotName, string? AgentPrompt);

    /// <summary>Creates a custom agent. Like every agent edit it is read at startup, so it takes effect on restart.</summary>
    [HttpPost]
    public IActionResult CreateAgent([FromBody] NewAgent req)
    {
        string name = (req.Name ?? "").Trim();
        if (name.Length is 0 or > 40 || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 _-]*$"))
            return BadRequest(new { error = "Use letters, numbers, spaces, - or _ (up to 40 characters)." });
        if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = $"'{name}' is the name of a built-in agent." });
        Server? server = persistentData.GetServers().FirstOrDefault(s => s.Name == req.ServerName) ?? persistentData.GetServers().FirstOrDefault();
        if (!persistentData.CreateCustomAgent(name, server?.Name ?? "", req.SlotName ?? server?.Slots.FirstOrDefault()?.Name, req.AgentPrompt ?? ""))
            return Conflict(new { error = $"An agent called '{name}' already exists." });
        return Ok(new { ok = true, restartRequired = true });
    }

    /// <summary>Deletes a custom agent. Shipped agents cannot be deleted, only switched off.</summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteAgent(string id)
    {
        if (!persistentData.RemoveCustomAgent(id))
            return NotFound(new { error = $"'{id}' is not a custom agent." });
        return Ok(new { ok = true, restartRequired = true });
    }

    /// <summary>Saves the prompts owned by no single agent (the MemoryAgent block, the Budgets footer).
    /// Read at startup, so it takes effect on restart.</summary>
    [HttpPatch("shared")]
    public IActionResult UpdateShared([FromBody] JsonObject req)
    {
        persistentData.MergeShared(req);
        return Ok(new { ok = true, restartRequired = true });
    }

    [HttpPatch("{id}")]
    public IActionResult UpdateAgent(string id, [FromBody] JsonObject req)
    {
        // Merged into the file rather than replacing the agent, so fields the panel doesn't know about survive.
        string name = id;
        if (!persistentData.MergeAgent(name, req))
            return NotFound(new { error = $"Agent '{name}' not found." });

        if (Llm is not null)
        {
            string? server = req["serverName"]?.GetValue<string>(), slot = req["slotName"]?.GetValue<string>();
            if (server is not null) Llm.AssignAgentServer(name, server);
            if (slot is { Length: > 0 }) Llm.AssignAgentSlot(name, slot);
        }

        return Ok(new { ok = true });
    }
}

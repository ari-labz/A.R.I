using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

/// <summary>The control panel's pipeline editor: the graphs in Pipelines.json and which one answers Dialogue threads.
/// Saving a graph is live; the active one has to validate, or it is refused.</summary>
[Route("api/v1/admin/pipelineGraphs")]
[ApiController]
public class PipelineGraphsController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>What ships: the same two steps the built-in Dialogue pipeline runs — recall memories, then reply with them.</summary>
    private static JsonObject Seed() => JsonNode.Parse("""
    {
      "active": null,
      "pipelines": [
        {
          "id": "default", "name": "Default",
          "nodes": [
            { "id": "start", "type": "start", "x": 20,  "y": 143 },
            { "id": "mem",   "type": "agent", "agent": "Memory",   "x": 270, "y": 20 },
            { "id": "dia",   "type": "agent", "agent": "Dialogue", "x": 560, "y": 110 },
            { "id": "end",   "type": "end",   "x": 900, "y": 143 }
          ],
          "edges": [
            { "from": "start", "to": "mem", "port": "history",  "mode": "complete" },
            { "from": "start", "to": "dia", "port": "history",  "mode": "complete" },
            { "from": "mem",   "to": "dia", "port": "memories", "mode": "complete" },
            { "from": "dia",   "to": "end", "port": "stream",   "mode": "stream" }
          ]
        }
      ]
    }
    """)!.AsObject();

    [HttpGet]
    public IActionResult Get()
    {
        JsonObject data = persistentData.GetPipelinesRaw() ?? Seed();
        return Ok(new
        {
            data,
            running = Llm?.ActivePipelineId,
            ports   = Llm?.AgentPorts() ?? new Dictionary<string, string[]>(),
            problems = Problems(data),
        });
    }

    [HttpPut]
    public IActionResult Put([FromBody] JsonObject body)
    {
        if (body["pipelines"] is not JsonArray)
            return BadRequest(new { error = "Expected { active, pipelines: [...] }." });

        Dictionary<string, List<string>> problems = Problems(body);
        string? active = body["active"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(active))
        {
            if (!problems.TryGetValue(active, out List<string>? p))
                return BadRequest(new { error = $"No pipeline with id '{active}'." });
            if (p.Count > 0)
                return BadRequest(new { error = "That pipeline can't be made active yet.", problems = p });
        }

        persistentData.SavePipelinesRaw(body);
        Llm?.ReloadPipelines();
        return Ok(new { ok = true, running = Llm?.ActivePipelineId, problems });
    }

    private Dictionary<string, List<string>> Problems(JsonObject data)
    {
        Dictionary<string, List<string>> result = new();
        if (Llm is null || data["pipelines"] is not JsonArray list) return result;
        foreach (JsonNode? p in list)
        {
            if (p is not JsonObject po) continue;
            using JsonDocument doc = JsonDocument.Parse(po.ToJsonString());
            result[po["id"]?.GetValue<string>() ?? ""] = Llm.ValidatePipeline(doc.RootElement);
        }
        return result;
    }
}

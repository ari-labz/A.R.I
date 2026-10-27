using ARI.API.Data;
using ARI.Common;
using ARI.LLM;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARI.API.Controllers;

/// <summary>The control panel's pipeline editor: the graphs in Pipelines.json. A pipeline is a way of building a reply, picked per
/// thread; saving is live. The built-in Default can be edited but never removed, and has to stay valid.</summary>
[Route("api/v1/admin/pipelineGraphs")]
[ApiController]
public class PipelineGraphsController(PersistentData persistentData) : ControllerBase
{
    private LLMModule? Llm => (LLMModule?)Modules.Llm;

    /// <summary>What ships: the same two steps the built-in Dialogue pipeline runs — recall memories, then reply with them.</summary>
    private static JsonObject Seed(bool withMemory = true)
    {
        JsonObject seed = JsonNode.Parse("""
    {
      "pipelines": [
        {
          "id": "default", "name": "Default",
          "nodes": [
            { "id": "start", "type": "start", "x": 20,  "y": 143 },
            { "id": "mem",   "type": "pipeline", "pipeline": "memory", "x": 270, "y": 20, "onError": "" },
            { "id": "dia",   "type": "agent", "agent": "Dialogue", "x": 560, "y": 110 },
            { "id": "end",   "type": "end",   "x": 900, "y": 143 }
          ],
          "edges": [
            { "from": "start", "to": "dia", "port": "history",  "mode": "complete" },
            { "from": "mem",   "to": "dia", "port": "memories", "mode": "complete" },
            { "from": "dia",   "to": "end", "port": "stream",   "mode": "stream" }
          ]
        }
      ]
    }
    """)!.AsObject();
        foreach (JsonNode? n in seed["pipelines"]![0]!["nodes"]!.AsArray().Where(n => n!["onError"] is not null)) n!["onError"] = PipelineGraph.MemoryUnavailableText;
        if (withMemory) return seed;
        // No Memory agent on this install: Dialogue answers straight from the conversation.
        JsonObject graph = seed["pipelines"]![0]!.AsObject();
        JsonArray nodes = graph["nodes"]!.AsArray(), edges = graph["edges"]!.AsArray();
        foreach (JsonNode? n in nodes.Where(n => n!["id"]!.GetValue<string>() == "mem").ToList()) nodes.Remove(n);
        foreach (JsonNode? e in edges.Where(e => e!["from"]!.GetValue<string>() == "mem" || e["to"]!.GetValue<string>() == "mem").ToList()) edges.Remove(e);
        return seed;
    }

    [HttpGet]
    public IActionResult Get()
    {
        JsonObject data = persistentData.GetPipelinesRaw() ?? Seed(Llm?.AgentPorts().ContainsKey("Memory") ?? true);
        PipelineGraph.UpgradeLegacyDefault(data);
        AddShipped(data);
        return Ok(new
        {
            data,
            ports    = Llm?.AgentPorts() ?? new Dictionary<string, string[]>(),
            optional = PipelineGraph.OptionalPorts,
            scripts  = (Llm?.ListScripts() ?? []).Where(x => x.Error is null).ToDictionary(x => x.Name, x => new { description = x.Description, inputs = x.Inputs, optionalInputs = x.OptionalInputs, outputs = x.Outputs }),
            problems = Problems(data),
        });
    }

    [HttpPut]
    public IActionResult Put([FromBody] JsonObject body)
    {
        if (body["pipelines"] is not JsonArray)
            return BadRequest(new { error = "Expected { active, pipelines: [...] }." });

        Dictionary<string, List<string>> problems = Problems(body);
        // The pipelines ARI ships can be edited but not deleted: Default is where every thread starts, and the others are steps
        // its own pipelines use.
        if (body["pipelines"] is JsonArray sent && PipelineGraph.ShippedIds.FirstOrDefault(id => !sent.Any(p => p?["id"]?.GetValue<string>() == id)) is { } missing)
            return BadRequest(new { error = $"The shipped pipeline '{missing}' can't be deleted." });

        // Default is the reply type every thread starts on and falls back to, so it must stay and must be able to run.
        if (!problems.TryGetValue(PipelineGraph.DefaultId, out List<string>? defaultProblems))
            return BadRequest(new { error = "The Default pipeline can't be deleted." });
        if (defaultProblems.Count > 0)
            return BadRequest(new { error = "The Default pipeline can't run yet, so it can't be saved like this.", problems = defaultProblems });

        persistentData.SavePipelinesRaw(body);
        Llm?.ReloadPipelines();
        return Ok(new { ok = true, problems });
    }

    /// <summary>Adds the pipelines ARI ships that the saved file doesn't have yet, so they show up in the editor.</summary>
    private static void AddShipped(JsonObject data)
    {
        if (data["pipelines"] is not JsonArray list) return;
        JsonArray shipped = JsonNode.Parse(PipelineGraph.ShippedJson)!["pipelines"]!.AsArray();
        foreach (JsonNode? p in shipped.ToList())
            if (!list.Any(x => x?["id"]?.GetValue<string>() == p!["id"]!.GetValue<string>()))
            {
                shipped.Remove(p);
                list.Add(p);
            }
    }

    private Dictionary<string, List<string>> Problems(JsonObject data)
    {
        if (Llm is null || data["pipelines"] is not JsonArray) return new();
        using JsonDocument doc = JsonDocument.Parse(data.ToJsonString());
        return Llm.ValidatePipelines(doc.RootElement);
    }
}

import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("a model is added, then changed with PATCH (only what is sent), then removed by its name", async () => {
  status(await master.post("/admin/models", { name: "patch-test.gguf", path: "patch-test.gguf" }), 200);
  status(await master.patch("/admin/models/patch-test.gguf", { notes: "my notes" }), 200);
  status(await master.patch("/admin/models/patch-test.gguf", { supportsThinking: true }), 200);
  const list = status(await master.get("/admin/models"), 200).json.models;
  const m = list.find(x => x.name === "patch-test.gguf");
  equal(m.notes, "my notes", "notes kept after the second change");
  equal(m.supportsThinking, true);
  equal(m.name, "patch-test.gguf", "the name cannot be changed by a patch");
  status(await master.patch("/admin/models/patch-test.gguf", { name: "renamed.gguf" }), 200);
  assert(status(await master.get("/admin/models"), 200).json.models.some(x => x.name === "patch-test.gguf"), "still has its name");
  status(await master.del("/admin/models/patch-test.gguf"), 200);
  status(await master.patch("/admin/models/patch-test.gguf", { notes: "x" }), 404);
});

test("a server is changed with PATCH and keeps the settings that were not sent", async () => {
  const created = status(await master.post("/admin/servers", { name: "patch-srv", port: 8210 }), 200).json;
  status(await master.patch(`/admin/servers/${created.id}`, { port: 8211 }), 200);
  let s = status(await master.get("/admin/models"), 200).json.servers.find(x => x.id === created.id);
  equal(s.port, 8211); equal(s.name, "patch-srv");
  status(await master.patch(`/admin/servers/${created.id}`, { name: "patch-srv-2" }), 200);
  s = status(await master.get("/admin/models"), 200).json.servers.find(x => x.id === created.id);
  equal(s.name, "patch-srv-2"); equal(s.port, 8211, "port untouched by a name-only change");
  equal((await master.patch(`/admin/servers/${created.id}`, { name: " " })).status, 400, "blank name");
  status(await master.del(`/admin/servers/${created.id}`), 200);
});

test("loading another model onto a server is an action on that server", async () => {
  const created = status(await master.post("/admin/servers", { name: "switch-srv", port: 8212 }), 200).json;
  equal((await master.post(`/admin/servers/${created.id}/switch`, {})).status, 400, "no model named");
  equal((await master.post(`/admin/servers/${created.id}/switch`, { modelName: "not-a-model.gguf" })).status, 404, "unknown model");
  status(await master.post("/admin/servers/switch", { serverId: created.id, modelName: "x" }), 404);
  status(await master.del(`/admin/servers/${created.id}`), 200);
});

test("an agent is changed with PATCH", async () => {
  const before = status(await master.get("/admin/agents"), 200).json;
  const dialogue = before.agents.find(a => a.name === "Dialogue");
  assert(dialogue, "Dialogue exists");
  status(await master.patch("/admin/agents/Dialogue", { agentPrompt: dialogue.agentPrompt ?? "" }), 200);
  status(await master.patch("/admin/agents/NoSuchAgent", { agentPrompt: "x" }), 404);
  status(await master.put("/admin/agents/Dialogue", {}), 404);
});

test("the old per-field model and startup routes are gone", async () => {
  status(await master.put("/admin/models/notes", { modelName: "x", notes: "y" }), 404);
  status(await master.put("/admin/models/thinking", { modelName: "x", supportsThinking: true }), 404);
  status(await master.put("/admin/models/reasoningEffort", { step: 1 }), 404);
});

test("the reasoning dial is part of the settings: anyone reads it, only an admin changes it", async () => {
  const before = status(await master.get("/admin/settings"), 200).json.reasoningEffort;
  status(await master.patch("/admin/settings", { reasoningEffort: 2 }), 200);
  equal(status(await guest.get("/reasoningEffort"), 200).json.step, 2);
  equal((await master.patch("/admin/settings", { reasoningEffort: 5 })).status, 400, "out of range");
  status(await master.patch("/admin/settings", { reasoningEffort: before }), 200);
  status(await guest.patch("/admin/settings", { reasoningEffort: 1 }), 403);
});

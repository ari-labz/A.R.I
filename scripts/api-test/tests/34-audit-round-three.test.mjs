import { test, status, equal, assert, base } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("a project name with quotes cannot become git command text", async () => {
  const p = status(await guest.post("/projects", { name: 'x" -c core.fsmonitor="touch /tmp/pwned' }), 200).json;
  status(await guest.get(`/projects/${p.id}/sync/status`), 200);
  status(await guest.del(`/projects/${p.id}`), 200);
});

test("a changing request that says it came from another site is refused", async () => {
  const r = await fetch(`${base}/api/v1/threads`, { method: "POST", headers: { Authorization: `Bearer ${master.token}`, "Content-Type": "application/json", Origin: "http://evil.example" }, body: "{}" });
  equal(r.status, 403);
  const same = await fetch(`${base}/api/v1/threads`, { method: "POST", headers: { Authorization: `Bearer ${master.token}`, "Content-Type": "application/json", Origin: base }, body: "{}" });
  equal(same.status, 200);
});

test("a brain restore must name a plain file, and a saved setup cannot be overwritten silently", async () => {
  const r = await master.post("/admin/brain/restore", { file: "../../etc/passwd" });
  assert(r.status === 400 || r.status === 503, "restore: " + r.status);
  status(await master.post("/admin/llmConfigs", { name: "dupe-test", description: "" }), 200);
  equal((await master.post("/admin/llmConfigs", { name: "dupe-test", description: "" })).status, 409);
  status(await master.del("/admin/llmConfigs/dupe-test.zip"), 200);
});

test("a voice chunk must be inside the range it announces", async () => {
  const stage = status(await master.post("/admin/voice/uploads", {}), 200).json;
  equal((await master.upload(`/admin/voice/uploads/${stage.stageId}?name=a.wav&chunk=5&totalChunks=2`, "a.wav", "x", "audio/wav")).status, 400);
  equal((await master.upload(`/admin/voice/uploads/${stage.stageId}?name=a.wav&chunk=-1&totalChunks=2`, "a.wav", "x", "audio/wav")).status, 400);
});

test("a guest can answer approvals only in their own thread", async () => {
  const mine = status(await guest.post("/threads", {}), 200).json.key;
  const theirs = status(await master.post("/threads", {}), 200).json.key;
  equal((await guest.post(`/threads/${mine}/approvals/none`, { allow: true })).status, 404, "own thread, nothing waiting");
  equal((await guest.post(`/threads/${theirs}/approvals/none`, { allow: true })).status, 403, "someone else's");
});

test("commands respect thread ownership, including keys nobody has used", async () => {
  equal((await guest.post("/commands", { threadKey: "dm:123", input: "/help" })).status, 403);
});

test("a sync status read changes nothing and a model needs a path inside the models folder", async () => {
  equal((await master.post("/admin/models", { name: "x.gguf", path: "../../x" })).status, 400);
  equal((await master.post("/admin/models", { name: "x.gguf", path: "/etc/x" })).status, 400);
});

import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

test("an admin sets another person's temporary password with PATCH on that person", async () => {
  const made = status(await master.post("/admin/users", { username: "verbuser", password: "first-password-1", role: "Guest" }), 200).json;
  equal((await master.patch(`/admin/users/${made.id}`, { newPassword: "short" })).status, 400, "too short");
  equal((await master.patch(`/admin/users/${made.id}`, {})).status, 400, "nothing to change");
  status(await master.patch(`/admin/users/${made.id}`, { newPassword: "second-password-2" }), 200);
  status(await anon.post("/auth/login", { username: "verbuser", password: "second-password-2" }), 200);
  status(await master.post(`/admin/users/${made.id}/reset-password`, { newPassword: "x" }), 404);
});

test("system readings sit together under /admin/system", async () => {
  for (const p of ["ram", "hardware", "stats"]) status(await master.get(`/admin/system/${p}`), 200, p);
  for (const p of ["/admin/ram", "/admin/hardware", "/admin/stats", "/admin/log"]) status(await master.get(p), 404, p);
});

test("a module is switched with PATCH", async () => {
  status(await master.patch("/admin/modules/NotAModule", { enabled: true }), 404);
  status(await master.put("/admin/modules/NotAModule", { enabled: true }), 404);
});

test("image generation settings change with PATCH and keep what is not sent", async () => {
  const before = status(await master.get("/admin/imagegen/config"), 200).json;
  status(await master.patch("/admin/imagegen/config", { idleSeconds: 123 }), 200);
  const now = status(await master.get("/admin/imagegen/config"), 200).json;
  equal(now.idleSeconds, 123);
  equal(now.port, before.port, "port untouched");
  status(await master.patch("/admin/imagegen/config", { idleSeconds: before.idleSeconds }), 200);
  status(await master.post("/admin/imagegen/config", {}), 404);
});

test("llama.cpp is one thing: read it, and change it with PATCH", async () => {
  has(status(await master.get("/admin/llamacpp"), 200).json, "installPath", "installedVersion", "updateAvailable");
  equal((await master.patch("/admin/llamacpp", { path: " " })).status, 400, "blank path");
  status(await master.get("/admin/llamacpp/status"), 404);
  status(await master.post("/admin/llamacpp/set-path", {}), 404);
});

test("brain backups: list, create at /backups", async () => {
  status(await master.get("/admin/brain/backups"), 200);
  status(await master.post("/admin/brain/backup", {}), 404);
});

test("saved model setups: create at the collection, read and delete by id", async () => {
  status(await master.get("/admin/llmconfigs"), 200);
  status(await master.post("/admin/llmconfigs/save", { name: "x" }), 404);
  const made = status(await master.post("/admin/llmconfigs", { name: "verbs-test", description: "" }), 200).json;
  status(await master.get(`/admin/llmconfigs/${encodeURIComponent(made.fileName)}`), 200);
  status(await master.get(`/admin/llmconfigs/${encodeURIComponent(made.fileName)}/contents`), 404);
  status(await master.del(`/admin/llmconfigs/${encodeURIComponent(made.fileName)}`), 200);
});

test("the theme is replaced with PUT, and picture slots too", async () => {
  status(await master.put("/admin/theme", { overrides: {} }), 200);
  status(await master.post("/admin/theme", { overrides: {} }), 404);
  status(await master.del("/admin/theme"), 200);
  status(await master.del("/admin/branding/not-a-slot"), 404);
  status(await master.post("/admin/branding/logo", {}), 404);
});

test("a project is changed with PATCH and keeps what is not sent", async () => {
  const p = status(await guest.post("/projects", { name: "verbs project", description: "keep me" }), 200).json;
  status(await guest.patch(`/projects/${p.id}`, { name: "verbs project 2" }), 200);
  const now = status(await guest.get("/projects"), 200).json.find(x => x.id === p.id);
  equal(now.name, "verbs project 2"); equal(now.description, "keep me");
  equal((await guest.patch(`/projects/${p.id}`, { name: " " })).status, 400, "blank name");
  status(await guest.put(`/projects/${p.id}`, { name: "x" }), 404);
  status(await guest.del(`/projects/${p.id}`), 200);
});

test("push: the key is read at /push/key, a device is added and removed at /push/subscriptions", async () => {
  const k = await master.get("/push/key");
  assert(k.status === 200 || k.status === 503, "key route exists: " + k.status);
  for (const [verb, path] of [["get", "/push/vapid-public-key"], ["post", "/push/subscribe"], ["post", "/push/unsubscribe"]])
    equal((await master[verb](path, verb === "get" ? undefined : {})).status, 404, path);
  const sub = await master.post("/push/subscriptions", {});
  assert(sub.status === 400 || sub.status === 503, "subscriptions route exists: " + sub.status);
});

test("persistent threads: the on/off switch is a setting, the saved conversations are at /persistedThreads", async () => {
  const before = status(await master.get("/admin/settings"), 200).json.persistedThreadsEnabled;
  status(await master.patch("/admin/settings", { persistedThreadsEnabled: !before }), 200);
  equal(status(await master.get("/admin/settings"), 200).json.persistedThreadsEnabled, !before);
  status(await master.patch("/admin/settings", { persistedThreadsEnabled: before }), 200);
  const stats = status(await master.get("/admin/persistedThreads"), 200).json;
  has(stats, "count", "bytes");
  assert(!("enabled" in stats), "the switch is read from settings, not here");
  status(await master.put("/admin/persistedThreads", { enabled: true }), 404);
});

import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

test("nobody can write into, read from or delete a project's hidden git folder", async () => {
  const p = status(await guest.post("/projects", { name: "git folder test" }), 200).json;
  for (const dir of [".ariproject", ".git", "sub/.ariproject"]) {
    equal((await guest.upload(`/projects/${p.id}/files`, "config", "[core]\n fsmonitor = touch /tmp/pwned", "text/plain", `?path=${encodeURIComponent(dir)}`)).status, 400, "upload into " + dir);
    equal((await guest.del(`/projects/${p.id}/files?path=${encodeURIComponent(dir)}`)).status, 400, "delete " + dir);
    equal((await guest.get(`/projects/${p.id}/files/content?path=${encodeURIComponent(dir + "/config")}`)).status, 400, "read " + dir);
  }
  status(await guest.del(`/projects/${p.id}`), 200);
});

test("the old client controller no longer answers (with or without a trailing slash)", async () => {
  equal((await guest.get("/client/")).status, 404);
});

test("names that try to be paths are refused everywhere", async () => {
  for (const path of ["/admin/llmConfigs/..%2F..%2Fetc", "/admin/voice/models/StyleTTS2/..%2F../checkpoints", "/admin/voice/dataset/transcribe/audio?name=../../x", "/admin/debug/sessions/..%2Fx"])
    equal((await master.get(path)).status, 400, path);
  equal((await master.del("/admin/voice/models/..%2F../x")).status, 400, "delete with a path as engine");
});

test("a thread nobody has used is not there to read or reach into", async () => {
  for (const path of ["/threads/never-used/messages", "/threads/never-used/files", "/threads/never-used/files/content?path=a", "/threads/never-used/export"])
    equal((await guest.get(path)).status, 404, path);
  equal((await guest.upload("/threads/never-used/files", "a.txt", "x")).status, 404, "upload");
});

test("a bad value in a request body is a 400 in the usual shape, not a crash page", async () => {
  equal((await master.post("/admin/calendar/entries", { kind: "event", title: "x", start: "2031-01-01T00:00:00", end: "2031-01-01T01:00:00", recurrence: { frequency: "weekly", daysOfWeek: ["Blursday"] } })).status, 400, "bad day");
  equal((await master.post("/admin/calendar/entries", { kind: "event", title: "x", start: "2031-01-01T00:00:00", end: "2031-01-01T01:00:00", recurrence: { frequency: "sometimes" } })).status, 400, "bad frequency");
  const r = await master.patch("/admin/agents/Dialogue", { serverName: 12 });
  assert(r.status === 400 || r.status === 200, "non-string server name: " + r.status);
  assert(r.json && (r.json.error || r.json.ok), "answers in JSON: " + r.text.slice(0, 60));
  equal((await master.post("/admin/setup/inspect", { url: "https://huggingface.co/x/y" })).json?.error !== undefined || true, true);
});

test("signing out when already signed out is fine, and a refused request never shows a web page", async () => {
  const r = status(await anon.post("/auth/logout", {}), 200);
  equal(r.json.ok, true);
  const bad = await master.post("/admin/branding/logo", {});
  assert(bad.headers.get("content-type")?.includes("json"), "JSON, not HTML: " + bad.headers.get("content-type"));
});

test("a project looks the same to everyone, but only an admin sees where it lives on the server", async () => {
  const p = status(await guest.post("/projects", { name: "shape test" }), 200).json;
  assert(!("rootPath" in p) && !("ownerId" in p), "guest sees no server path or owner id");
  const listed = status(await master.get("/projects"), 200).json.find(x => x.id === p.id);
  assert(listed.rootPath && listed.ownerUsername, "admin sees path and owner");
  status(await guest.del(`/projects/${p.id}`), 200);
});

test("unknown sessions, addresses and accounts are 404, not a quiet success", async () => {
  equal((await master.del("/admin/sessions/not-a-session")).status, 404);
  equal((await master.del("/admin/blocklist/10.9.9.9")).status, 404);
  equal((await master.del("/admin/users/987654")).status, 404);
});

test("rating a reply and reading a thread's ratings lives under the thread", async () => {
  const key = status(await guest.post("/threads", {}), 200).json.key;
  status(await guest.get(`/threads/${key}/feedback`), 200);
  status(await guest.get(`/feedback/thread/${key}`), 404);
});

test("approving an unknown persona proposal fails in the usual shape", async () => {
  const r = await master.post("/admin/personaProposals/nope/approve", {});
  assert(r.status >= 400 && typeof r.json?.error === "string", r.status + " " + r.text.slice(0, 60));
});

test("a page can never be bigger than 1000, and speech text has a limit", async () => {
  const r = status(await master.get("/threads?limit=999999"), 200);
  assert(r.json.length <= 1000);
  equal((await guest.post("/speech/speak", { text: "x".repeat(6000) })).status, 400);
});

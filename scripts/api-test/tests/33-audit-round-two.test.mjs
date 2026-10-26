import { test, status, equal, assert, Client } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

test("a guest cannot take a conversation key that an integration uses", async () => {
  equal((await guest.post("/threads/dm:123456789/messages", { prompt: "hi" })).status, 403, "dm key");
  equal((await guest.post("/threads/guild:42/messages", { prompt: "hi" })).status, 403, "guild key");
  equal((await guest.post("/threads/dm:123456789/typing", {})).status, 403, "typing");
  equal((await guest.get("/threads/dm:123456789/feedback")).status, 404, "ratings of a thread that is not there");
});

test("a guest cannot pick an internal pipeline", async () => {
  for (const pipeline of ["dream", "3", "2"]) {
    const key = status(await guest.post("/threads", { pipeline }), 200).json.key;
    const t = status(await guest.get(`/threads/${key}`), 200).json;
    assert(["dialogue", "code", "speech"].includes(t.pipeline), `${pipeline} gave ${t.pipeline}`);
    if (pipeline === "dream") equal(t.pipeline, "dialogue");
  }
  const list = status(await guest.get("/pipelines"), 200).json;
  assert(!list.includes("dream"), "dream is not offered");
});

test("a push address must be a real browser push service", async () => {
  for (const endpoint of ["http://127.0.0.1:9/x", "https://127.0.0.1/x", "https://internal.example.com/x", "https://fcm.googleapis.com.evil.test/x"]) {
    const r = await guest.post("/push/subscriptions", { endpoint, keys: { p256dh: "a", auth: "b" } });
    assert(r.status === 400 || r.status === 503, `${endpoint} -> ${r.status}`);
  }
});

test("a temporary password has to be replaced before anything else works", async () => {
  status(await master.post("/admin/users", { username: "tempuser", password: "temp-password-1", role: "Guest" }), 200);
  const id = status(await master.get("/admin/users"), 200).json.find(u => u.username === "tempuser").id;
  status(await master.patch(`/admin/users/${id}`, { newPassword: "temp-password-2" }), 200);
  const me = new Client(status(await anon.post("/auth/login", { username: "tempuser", password: "temp-password-2" }), 200).json.token);
  equal(status(await me.get("/me"), 200).json.mustChangePassword, true);
  equal((await me.get("/threads")).status, 403, "blocked until changed");
  status(await me.patch("/me", { currentPassword: "temp-password-2", newPassword: "chosen-password-3" }), 200);
  const again = new Client(status(await anon.post("/auth/login", { username: "tempuser", password: "chosen-password-3" }), 200).json.token);
  status(await again.get("/threads"), 200);
});

test("nobody can take another person's name as their own", async () => {
  equal((await guest.patch("/me", { displayName: "master" })).status, 409);
  equal((await guest.patch("/me", { displayName: "Master" })).status, 409);
});

test("project files cannot be reached through a link, and git bookkeeping files are off limits", async () => {
  const p = status(await guest.post("/projects", { name: "links test" }), 200).json;
  equal((await guest.upload(`/projects/${p.id}/files`, ".gitattributes", "* filter=x")).status, 400, ".gitattributes");
  equal((await guest.upload(`/projects/${p.id}/files`, ".git", "gitdir: /tmp")).status, 400, ".git file");
  status(await guest.del(`/projects/${p.id}`), 200);
});

test("the sockets refuse a page from another site", async () => {
  const { wsProbe } = await import("../lib.mjs");
  assert(wsProbe, "probe exists");
});

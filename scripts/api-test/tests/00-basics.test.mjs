import { test, status, has, equal, assert } from "../lib.mjs";
const { anon, master, guest } = globalThis.ari;

test("the master can read their own profile", async () => {
  const r = status(await master.get("/me"), 200);
  has(r.json, "username", "role");
  equal(r.json.username, "master");
  equal(r.json.role, "Admin");
});

test("a request with no sign-in is refused", async () => {
  status(await anon.get("/threads"), 401);
});

test("a guest cannot reach an admin route", async () => {
  status(await guest.get("/admin/users"), 403);
});

test("a thread can be created and listed", async () => {
  const made = status(await master.post("/threads", {}), 200);
  has(made.json, "key");
  const list = status(await master.get("/threads"), 200);
  assert(list.json.some(t => t.key === made.json.key), "new thread should be in the list");
});

test("a message gets a reply from the stand-in model", async () => {
  const made = status(await master.post("/threads", {}), 200);
  const key = made.json.key;
  const r = await master.stream(`/threads/${key}/messages`, { prompt: "hello there" });
  status(r, 200);
  assert(r.events.some(e => e.includes("[DONE]")), "stream should end with [DONE]: " + JSON.stringify(r.events.slice(-3)));
  assert(r.events.some(e => e.includes("Echo: hello there")), "reply should echo the message: " + JSON.stringify(r.events.slice(-3)));
});

test("every route lives under /api/v1, and the old unprefixed paths are gone", async () => {
  const { base } = await import("../lib.mjs");
  for (const old of ["/threads", "/me", "/admin/users", "/api/info"]) {
    const res = await fetch(base + old, { headers: { Authorization: `Bearer ${master.token}` } });
    const body = await res.text();
    assert(res.status === 404 || !res.headers.get("content-type")?.includes("json"), `${old} should not answer as an API route (got ${res.status}: ${body.slice(0, 60)})`);
  }
  status(await master.get("/threads"), 200);
});

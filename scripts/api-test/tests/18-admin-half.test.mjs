import { test, status, assert } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

const adminReads = [
  "/admin/agents", "/admin/models", "/admin/voice/status", "/admin/imagegen/status", "/admin/modules",
  "/admin/pipelineGraphs", "/admin/commandAllowlist", "/admin/feedback", "/admin/debug/sessions",
  "/admin/persistedThreads", "/admin/settings", "/admin/users",
];
// The log is a never-ending stream, so it is checked for who may open it, but the master never waits for it to finish.
const streams = ["/admin/log"];

test("a guest is refused on every admin route, whatever it is for", async () => {
  for (const path of [...adminReads, ...streams]) status(await guest.get(path), 403, path);
});

test("a request with no sign-in is refused on every admin route", async () => {
  for (const path of [...adminReads, ...streams]) status(await anon.get(path), 401, path);
});

test("the master is let through to the admin routes (they answer, they are not blocked)", async () => {
  for (const path of adminReads) {
    const r = await master.get(path);
    assert(r.status !== 401 && r.status !== 403 && r.status !== 404, `${path} answered ${r.status}`);
  }
});

test("a brand-new route under /admin is protected just by where it is, even one that does not exist yet", async () => {
  status(await guest.get("/admin/somethingAddedLater"), 403);
});

test("the few things a guest's chat page needs sit outside /admin and work for a guest", async () => {
  status(await guest.get("/reasoningEffort"), 200);
  status(await guest.post("/speech/splitSentences", { text: "One. Two." }), 200);
});

test("changing the reasoning dial stays admin only", async () => {
  status(await guest.put("/admin/models/reasoningEffort", { step: 1 }), 403);
});

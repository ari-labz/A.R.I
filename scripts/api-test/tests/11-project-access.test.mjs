import { test, status, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("a guest cannot attach someone else's project to a thread", async () => {
  const masters = status(await master.post("/projects", { name: "Master private" }), 200).json;
  const r = await guest.post("/threads", { projectId: masters.id });
  equal(r.status, 404, "status");
  // and nothing was created for them: the thread list is unchanged by the failed call
});

test("a guest can attach their own project", async () => {
  const mine = status(await guest.post("/projects", { name: "Guest own" }), 200).json;
  const r = status(await guest.post("/threads", { projectId: mine.id }), 200);
  assert(r.json.key, "should get a thread key");
});

test("an admin can attach a guest's project", async () => {
  const theirs = status(await guest.post("/projects", { name: "Guest second" }), 200).json;
  status(await master.post("/threads", { projectId: theirs.id }), 200);
});

test("a project id that does not exist is reported, not ignored", async () => {
  equal((await master.post("/threads", { projectId: "does-not-exist" })).status, 404, "status");
});

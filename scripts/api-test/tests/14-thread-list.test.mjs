import { test, status, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("a guest cannot ask for the internal thread list", async () => {
  equal((await guest.get("/threads?includeInternal=true")).status, 403, "status");
});

test("a guest only sees their own threads, never the master's", async () => {
  const mine  = status(await guest.post("/threads", {}), 200).json.key;
  const other = status(await master.post("/threads", {}), 200).json.key;
  const list  = status(await guest.get("/threads"), 200).json;
  assert(list.some(t => t.key === mine), "own thread should be listed");
  assert(!list.some(t => t.key === other), "someone else's thread must not be listed");
});

test("the admin can still see the internal thread list", async () => {
  status(await master.get("/threads?includeInternal=true"), 200);
});

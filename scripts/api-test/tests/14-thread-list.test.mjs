import { test, status, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("the internal thread list is an admin route, and asking the ordinary list for it changes nothing", async () => {
  equal((await guest.get("/admin/debug/threads")).status, 403, "status");
  const mine = status(await guest.post("/threads", {}), 200).json.key;
  const list = status(await guest.get("/threads?includeInternal=true"), 200).json;
  assert(list.every(t => t.isInternal !== true), "no internal threads for a guest");
  assert(list.some(t => t.key === mine));
});

test("a guest only sees their own threads, never the master's", async () => {
  const mine  = status(await guest.post("/threads", {}), 200).json.key;
  const other = status(await master.post("/threads", {}), 200).json.key;
  const list  = status(await guest.get("/threads"), 200).json;
  assert(list.some(t => t.key === mine), "own thread should be listed");
  assert(!list.some(t => t.key === other), "someone else's thread must not be listed");
});

test("the admin can still see the internal thread list", async () => {
  status(await master.get("/admin/debug/threads"), 200);
});

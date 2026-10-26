import { test, status, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("a list can be taken in slices with limit and offset, and says how many there are in all", async () => {
  for (let i = 0; i < 5; i++) status(await guest.post("/threads", {}), 200);
  const all = status(await guest.get("/threads"), 200);
  const total = all.json.length;
  assert(total >= 5, "have threads");
  equal(all.headers.get("x-total-count"), String(total), "total header on an unsliced list");
  const first = status(await guest.get("/threads?limit=2"), 200);
  equal(first.json.length, 2); equal(first.headers.get("x-total-count"), String(total));
  const next = status(await guest.get("/threads?limit=2&offset=2"), 200);
  equal(next.json[0].key, all.json[2].key, "the second slice starts where the first ended");
  const past = status(await guest.get(`/threads?offset=${total + 10}`), 200);
  equal(past.json.length, 0);
});

test("the other long lists page the same way", async () => {
  for (const path of ["/admin/users", "/admin/sessions", "/projects", "/admin/feedback"]) {
    const r = status(await master.get(`${path}?limit=1`), 200, path);
    assert(r.headers.get("x-total-count") !== null, `${path} should say its total`);
  }
  const key = status(await master.post("/threads", {}), 200).json.key;
  await master.stream(`/threads/${key}/messages`, { prompt: "paging please" });
  const m = status(await master.get(`/threads/${key}/messages?limit=1`), 200);
  equal(m.json.length, 1); assert(Number(m.headers.get("x-total-count")) >= 2, "messages report their total");
});

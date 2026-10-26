import { test, status, equal, Client } from "../lib.mjs";
const { master, anon } = globalThis.ari;

test("a token in the address works for a read (the way an event stream or image link needs)", async () => {
  status(await anon.get(`/me?token=${encodeURIComponent(master.token)}`), 200);
});

test("a token in the address is refused for anything that changes data", async () => {
  const t = encodeURIComponent(master.token);
  equal((await anon.post(`/threads?token=${t}`, {})).status, 401, "POST status");
  equal((await anon.del(`/threads/none?token=${t}`)).status, 401, "DELETE status");
});

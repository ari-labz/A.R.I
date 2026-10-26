import { test, equal } from "../lib.mjs";
const { master, anon } = globalThis.ari;

test("a sign-in token in the address is never accepted, not even for a read", async () => {
  const t = encodeURIComponent(master.token);
  equal((await anon.get(`/me?token=${t}`)).status, 401, "GET status");
  equal((await anon.get(`/threads?token=${t}`)).status, 401, "GET list status");
  equal((await anon.post(`/threads?token=${t}`, {})).status, 401, "POST status");
  equal((await anon.del(`/threads/none?token=${t}`)).status, 401, "DELETE status");
});

test("the token in the Authorization header still works", async () => {
  equal((await master.get("/me")).status, 200);
});

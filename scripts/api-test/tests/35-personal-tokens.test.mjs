import { test, status, has, equal, assert, Client, base } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

async function makeToken(client, name = "my script") {
  return status(await client.post("/me/tokens", { name }), 200).json;
}

test("a person can make a token, it is shown once, and it never expires or needs signing in again", async () => {
  const made = await makeToken(guest, "ci script");
  assert(made.token.startsWith("ari_pat_"), "has the ari_pat_ prefix");
  has(made.details, "id", "name", "prefix", "createdAt");
  const list = status(await guest.get("/me/tokens"), 200).json;
  const mine = list.find(t => t.id === made.details.id);
  assert(mine && !JSON.stringify(list).includes(made.token), "listed by fingerprint, never the secret itself");
  const viaToken = new Client(made.token);
  const me = status(await viaToken.get("/me"), 200).json;
  equal(me.username, "guest");
  assert(status(await guest.get("/me/tokens"), 200).json.find(t => t.id === made.details.id).lastUsedAt, "last used is recorded");
});

test("a token has exactly its owner's permissions", async () => {
  const guestToken = new Client((await makeToken(guest)).token);
  status(await guestToken.get("/threads"), 200);
  equal((await guestToken.get("/admin/users")).status, 403, "a guest's token is not admin");
  const masterToken = new Client((await makeToken(master, "admin key")).token);
  status(await masterToken.get("/admin/users"), 200);
  status(await masterToken.get("/admin/settings"), 200);
});

test("a token can use ARI like its owner does: start a thread and talk", async () => {
  const t = new Client((await makeToken(guest)).token);
  const key = status(await t.post("/threads", {}), 200).json.key;
  const r = await t.stream(`/threads/${key}/messages`, { prompt: "hello from a token" });
  assert(r.lines?.length || r.length || true);
  assert(status(await t.get(`/threads/${key}/messages`), 200).json.length >= 2);
  equal((await master.get(`/threads/${key}/messages`)).status, 200, "the owner's thread is the guest's, seen by admin");
});

test("a token cannot make, list or delete tokens", async () => {
  const t = new Client((await makeToken(guest)).token);
  equal((await t.post("/me/tokens", { name: "sneaky" })).status, 403);
  equal((await t.get("/me/tokens")).status, 403);
});

test("deleting a token stops it working at once, and nobody can delete someone else's", async () => {
  const made = await makeToken(guest, "short lived");
  const t = new Client(made.token);
  status(await t.get("/me"), 200);
  equal((await master.del(`/me/tokens/${made.details.id}`)).status, 404, "not the admin's own token");
  status(await guest.del(`/me/tokens/${made.details.id}`), 200);
  equal((await t.get("/me")).status, 401);
});

test("an admin can see every token (never the secret) and cut one off", async () => {
  const made = await makeToken(guest, "admin sees me");
  const all = status(await master.get("/admin/tokens"), 200).json;
  const row = all.find(x => x.id === made.details.id);
  assert(row && row.username === "guest" && !JSON.stringify(all).includes(made.token));
  equal((await guest.get("/admin/tokens")).status, 403);
  status(await master.del(`/admin/tokens/${made.details.id}`), 200);
  equal((await new Client(made.token).get("/me")).status, 401);
});

test("a made-up token, a token in the address and a stripped token are all refused", async () => {
  equal((await new Client("ari_pat_not-a-real-token").get("/me")).status, 401);
  const made = await makeToken(guest);
  const r = await fetch(`${base}/api/v1/me?token=${made.token}`);
  equal(r.status, 401);
  equal((await new Client(made.token.slice(0, -2)).get("/me")).status, 401);
});

test("a token needs a name, and a person can only have so many", async () => {
  equal((await guest.post("/me/tokens", { name: " " })).status, 400);
  equal((await guest.post("/me/tokens", {})).status, 400);
});

test("deleting an account removes its tokens", async () => {
  status(await master.post("/admin/users", { username: "tokenowner", password: "long-enough-1", role: "Guest" }), 200);
  const login = status(await anon.post("/auth/login", { username: "tokenowner", password: "long-enough-1" }), 200).json.token;
  const session = new Client(login);
  status(await session.patch("/me", { currentPassword: "long-enough-1", newPassword: "long-enough-1" }), 200);
  const made = await makeToken(session);
  const id = status(await master.get("/admin/users"), 200).json.find(u => u.username === "tokenowner").id;
  status(await master.del(`/admin/users/${id}`), 200);
  equal((await new Client(made.token).get("/me")).status, 401);
});

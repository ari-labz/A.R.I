import { test, status, has, equal, assert, Client } from "../lib.mjs";
const { master, anon } = globalThis.ari;

async function newPerson(name, password = "long-enough-1") {
  status(await master.post("/admin/users", { username: name, password, role: "Guest" }), 200);
  const login = status(await anon.post("/auth/login", { username: name, password }), 200);
  return new Client(login.json.token);
}

test("/me says who you are and which devices you are signed in on", async () => {
  const me = await newPerson("meone");
  const r = status(await me.get("/me"), 200);
  has(r.json, "id", "username", "role", "displayName", "mustChangePassword", "sessions");
  equal(r.json.username, "meone");
  assert(r.json.sessions.length >= 1, "this device should be listed");
});

test("the old /auth/me, /auth/change-password and /user routes are gone", async () => {
  const me = await newPerson("metwo");
  status(await me.get("/auth/me"), 404);
  status(await me.post("/auth/change-password", { currentPassword: "long-enough-1", newPassword: "another-long-1" }), 404);
  status(await me.get("/user/preferences"), 404);
});

test("the display name changes on its own, with no password", async () => {
  const me = await newPerson("methree");
  status(await me.patch("/me", { displayName: "Three" }), 200);
  equal(status(await me.get("/me"), 200).json.displayName, "Three");
  equal((await me.patch("/me", { displayName: "   " })).status, 400, "blank name");
});

test("a new password needs the right current password, and signs the other devices out", async () => {
  const me = await newPerson("mefour");
  const other = new Client(status(await anon.post("/auth/login", { username: "mefour", password: "long-enough-1" }), 200).json.token);
  equal((await me.patch("/me", { newPassword: "brand-new-pass-1" })).status, 400, "no current password");
  equal((await me.patch("/me", { currentPassword: "wrong-password", newPassword: "brand-new-pass-1" })).status, 400, "wrong current password");
  status(await me.patch("/me", { currentPassword: "long-enough-1", newPassword: "brand-new-pass-1" }), 200);
  status(await anon.post("/auth/login", { username: "mefour", password: "brand-new-pass-1" }), 200);
  status(await me.get("/me"), 200);
  status(await other.get("/me"), 401);
});

test("a new username needs the password, is checked, and signs every device out", async () => {
  const me = await newPerson("mefive");
  equal((await me.patch("/me", { username: "x" })).status, 400, "no password");
  const r = status(await me.patch("/me", { username: "mefive-renamed", currentPassword: "long-enough-1" }), 200);
  equal(r.json.signInAgain, true);
  status(await me.get("/me"), 401);
  status(await anon.post("/auth/login", { username: "mefive-renamed", password: "long-enough-1" }), 200);
});

test("a refused change changes nothing", async () => {
  const me = await newPerson("mesix");
  equal((await me.patch("/me", { displayName: "Sneaky", currentPassword: "long-enough-1", newPassword: "short" })).status, 400, "status");
  assert(status(await me.get("/me"), 200).json.displayName !== "Sneaky", "display name must not have changed");
});

test("another device can be signed out, but not the one you are using", async () => {
  const me = await newPerson("meseven");
  const second = status(await anon.post("/auth/login", { username: "meseven", password: "long-enough-1" }), 200).json.token;
  const sessions = status(await me.get("/me"), 200).json.sessions;
  assert(sessions.length >= 2, "two devices");
  const mine = sessions.find(s => s.deviceHint && s.sessionId);
  // The one in use cannot be revoked this way.
  const results = [];
  for (const s of sessions) results.push((await me.del(`/me/sessions/${s.sessionId}`)).status);
  assert(results.includes(400), "the current device should be refused");
  assert(results.includes(200), "the other device should be signed out");
});

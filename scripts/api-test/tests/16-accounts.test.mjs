import { test, status, equal, assert, Client } from "../lib.mjs";
const { master, anon } = globalThis.ari;

test("an account needs a username of 2 to 40 plain characters", async () => {
  for (const bad of ["x", "has space", "a".repeat(41), "bad/name"])
    equal((await master.post("/admin/users", { username: bad, password: "long-enough-1", role: "Guest" })).status, 400, `status for "${bad}"`);
});

test("an account needs a password of at least 8 characters, wherever it is set", async () => {
  const r = await master.post("/admin/users", { username: "shortpw", password: "1234567", role: "Guest" });
  equal(r.status, 400, "create status");
  assert(JSON.stringify(r.json).includes("8"), "message should say 8: " + r.text);

  const made = status(await master.post("/admin/users", { username: "pwuser", password: "password-one", role: "Guest" }), 200).json;
  equal((await master.patch(`/admin/users/${made.id}`, { newPassword: "short" })).status, 400, "reset status");

  const login = status(await anon.post("/auth/login", { username: "pwuser", password: "password-one" }), 200);
  const me = new Client(login.json.token);
  equal((await me.patch("/me", { currentPassword: "password-one", newPassword: "short" })).status, 400, "change status");
  status(await me.patch("/me", { currentPassword: "password-one", newPassword: "password-two" }), 200);
  status(await anon.post("/auth/login", { username: "pwuser", password: "password-two" }), 200);
});

test("a username cannot be taken twice", async () => {
  status(await master.post("/admin/users", { username: "dupe", password: "long-enough-1", role: "Guest" }), 200);
  equal((await master.post("/admin/users", { username: "dupe", password: "long-enough-1", role: "Guest" })).status, 409, "status");
});

test("renaming yourself follows the same username rule", async () => {
  status(await master.post("/admin/users", { username: "renamer", password: "long-enough-1", role: "Guest" }), 200);
  const login = status(await anon.post("/auth/login", { username: "renamer", password: "long-enough-1" }), 200);
  const me = new Client(login.json.token);
  equal((await me.patch("/me", { username: "bad name", currentPassword: "long-enough-1" })).status, 400, "status");
});

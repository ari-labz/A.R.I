// Runs every test in ./tests against a running ARI. Start it with run.sh.
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { Client, readSetupToken, runAll, assert, status } from "./lib.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));

// ── shared setup: create the first (Master) account, a guest, and sign both in ─────
const anon = new Client();
const setup = await anon.get("/setup/status");
status(setup, 200, "setup status");
assert(setup.json.needsAccount === true, "a fresh install should need an account");
const created = await anon.post("/setup/account", { token: readSetupToken(), username: "master", password: "master-password-1" });
status(created, 200, "create master");
export const master = new Client(created.json.token);
const guestMade = await master.post("/admin/users", { username: "guest", password: "guest-password-1", role: "Guest" });
status(guestMade, 200, "create guest");
const guestLogin = await anon.post("/auth/login", { username: "guest", password: "guest-password-1" });
status(guestLogin, 200, "guest login");
export const guest = new Client(guestLogin.json.token);

globalThis.ari = { anon, master, guest };

for (const f of fs.readdirSync(path.join(here, "tests")).filter(f => f.endsWith(".test.mjs")).sort())
  await import(path.join(here, "tests", f));

const ok = await runAll();
process.exit(ok ? 0 : 1);

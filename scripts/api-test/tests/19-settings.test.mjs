import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("all general settings are read from one address", async () => {
  const r = status(await master.get("/admin/settings"), 200);
  has(r.json, "userName", "conventions", "safeModePrompt", "persona", "dreamingEnabled", "engramTurns", "logging");
  has(r.json.logging, "enabled", "includeFullRequests", "includeToolResults", "retentionDays");
});

test("sending only some settings changes only those", async () => {
  const before = status(await master.get("/admin/settings"), 200).json;
  const r = status(await master.patch("/admin/settings", { conventions: "Use tabs.", engramTurns: 7 }), 200);
  equal(r.json.settings.conventions, "Use tabs.");
  equal(r.json.settings.engramTurns, 7);
  equal(r.json.settings.persona, before.persona, "persona untouched");
  equal(r.json.settings.safeModePrompt, before.safeModePrompt, "safe mode prompt untouched");
  equal(r.json.restartRequired, false);
  const after = status(await master.get("/admin/settings"), 200).json;
  equal(after.conventions, "Use tabs.");
  equal(after.engramTurns, 7);
  await master.patch("/admin/settings", { conventions: before.conventions, engramTurns: before.engramTurns });
});

test("the text settings can each be set and cleared", async () => {
  for (const key of ["persona", "safeModePrompt", "userName"]) {
    const r = status(await master.patch("/admin/settings", { [key]: "hello" }), 200);
    equal(r.json.settings[key], "hello", key);
    const cleared = status(await master.patch("/admin/settings", { [key]: "" }), 200);
    equal(cleared.json.settings[key], "", key);
  }
});

test("logging settings are saved and flagged as needing a restart", async () => {
  const before = status(await master.get("/admin/settings"), 200).json.logging;
  const r = status(await master.patch("/admin/settings", { logging: { ...before, retentionDays: 12 } }), 200);
  equal(r.json.settings.logging.retentionDays, 12);
  equal(r.json.restartRequired, true);
  await master.patch("/admin/settings", { logging: before });
});

test("a nonsense setting is refused and nothing is changed", async () => {
  const before = status(await master.get("/admin/settings"), 200).json;
  status(await master.patch("/admin/settings", { persona: "should not stick", engramTurns: -1 }), 400);
  equal(status(await master.get("/admin/settings"), 200).json.persona, before.persona);
});

test("the old one-setting-per-address routes are gone", async () => {
  for (const old of ["/admin/conventions", "/admin/username", "/admin/persona", "/admin/dreaming", "/admin/safemode-prompt", "/admin/brain/engram-interval", "/admin/settings/logging"])
    status(await master.get(old), 404, old);
});

test("a guest cannot read or change settings", async () => {
  status(await guest.get("/admin/settings"), 403);
  status(await guest.patch("/admin/settings", { persona: "x" }), 403);
});

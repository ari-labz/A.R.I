import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

test("/health answers with no sign-in and says nothing else", async () => {
  const r = status(await anon.get("/health"), 200);
  equal(r.json.status, "ok");
  equal(Object.keys(r.json).length, 1);
});

test("only the two sign-in-free setup steps stay under /setup", async () => {
  has(status(await anon.get("/setup/status"), 200).json, "needsAccount", "setupComplete", "tutorialComplete");
  equal((await anon.post("/setup/account", { token: "x", username: "someone", password: "long-enough-1" })).status, 409, "an account already exists");
  for (const [verb, path] of [["get", "/setup/hardware"], ["post", "/setup/inspect"], ["post", "/setup/model"], ["get", "/setup/progress"],
      ["post", "/setup/skip-model"], ["post", "/setup/tutorial-complete"], ["post", "/setup/restart-tutorial"]])
    equal((await master[verb](path, verb === "get" ? undefined : {})).status, 404, `${verb} ${path}`);
});

test("the rest of the wizard is under /admin/setup, and a guest is kept out", async () => {
  has(status(await master.get("/admin/setup/progress"), 200).json, "phase");
  equal((await master.post("/admin/setup/inspect", { url: "https://example.com/not-hugging-face" })).status, 400, "not a Hugging Face link");
  for (const [verb, path] of [["get", "/admin/setup/progress"], ["post", "/admin/setup/inspect"], ["post", "/admin/setup/model"], ["patch", "/admin/setup"]])
    status(await guest[verb](path, verb === "get" ? undefined : {}), 403, path);
});

test("how far the wizard has got is changed with one PATCH", async () => {
  const before = status(await anon.get("/setup/status"), 200).json;
  const r = status(await master.patch("/admin/setup", { tutorialComplete: false }), 200).json;
  equal(r.tutorialComplete, false);
  equal(status(await anon.get("/setup/status"), 200).json.tutorialComplete, false);
  status(await master.patch("/admin/setup", { tutorialComplete: true }), 200);
  equal(status(await anon.get("/setup/status"), 200).json.tutorialComplete, true);
  if (!before.tutorialComplete) status(await master.patch("/admin/setup", { tutorialComplete: false }), 200);
});

test("there is one hardware report, with both the usable-memory figures and the raw list", async () => {
  const r = status(await master.get("/admin/system/hardware"), 200).json;
  has(r, "totalBytes", "usableBytes", "availableBytes", "unified", "note", "totalRamBytes", "gpus");
});

import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("the voice ARI speaks with is read and changed at /voice/playback", async () => {
  const r = status(await master.get("/admin/voice/playback"), 200);
  has(r.json, "model", "engine", "ready", "defaultModel", "speed", "pauseScale");
  equal(r.json.ready, false, "the voice module is off in the test setup");
  equal((await master.patch("/admin/voice/playback", { speed: 1.2 })).status, 503, "no voice module to change");
  equal((await master.patch("/admin/voice/playback", { defaultModel: "no-such-voice" })).status, 404, "unknown voice");
  equal((await master.patch("/admin/voice/playback", { defaultModel: "../escape" })).status, 400, "unsafe name");
});

test("training has its own folder: status, start, progress, stop and resume", async () => {
  const r = status(await master.get("/admin/voice/training"), 200);
  equal(r.json.idle, true);
  const stage = status(await master.post("/admin/voice/uploads", {}), 200).json;
  has(stage, "stageId", "stagingPath");
  const up = await master.upload(`/admin/voice/uploads/${stage.stageId}?name=a.wav&chunk=0&totalChunks=1`, "a.wav", "RIFFxxxx", "audio/wav");
  assert(up.status !== 404, "the upload route exists: " + up.status);
});

test("voice models, and what can be done to one, live under /voice/models", async () => {
  status(await master.get("/admin/voice/models"), 200);
  status(await master.get("/admin/voice/models/StyleTTS2/none/checkpoints"), 404);
  status(await master.del("/admin/voice/models/StyleTTS2/none"), 404);
  status(await master.del("/admin/voice/models/StyleTTS2/none/resume"), 404);
});

test("preparing a recording for training lives under /voice/dataset", async () => {
  status(await master.get("/admin/voice/dataset/status"), 200);
  status(await master.get("/admin/voice/dataset/transcribe/status"), 200);
});

test("the old loose voice routes are gone", async () => {
  for (const [verb, path] of [["get", "/admin/voice/status"], ["get", "/admin/voice/active"], ["get", "/admin/voice/settings"],
      ["get", "/admin/voice/progress"], ["get", "/admin/voice/engine-params"], ["get", "/admin/voice/transcribe/status"],
      ["post", "/admin/voice/stage"], ["post", "/admin/voice/train"], ["post", "/admin/voice/switch"], ["put", "/admin/voice/default"]])
    equal((await master[verb](path, verb === "get" ? undefined : {})).status, 404, `${verb} ${path}`);
});

test("a guest is kept out of all of it", async () => {
  for (const path of ["/admin/voice/playback", "/admin/voice/training", "/admin/voice/models", "/admin/voice/dataset/status"])
    status(await guest.get(path), 403, path);
});

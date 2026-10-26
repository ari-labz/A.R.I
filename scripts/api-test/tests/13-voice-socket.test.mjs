import { test, status, equal, wsProbe } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

test("the microphone socket refuses a request with no sign-in", async () => {
  equal(await wsProbe("/listener/stream?threadKey=abc"), 401, "status");
});

test("a guest cannot speak into another person's thread", async () => {
  const key = status(await master.post("/threads", {}), 200).json.key;
  equal(await wsProbe(`/listener/stream?threadKey=${key}`, guest.token), 403, "status");
});

test("a person can reach the socket for their own thread (503 only because the listener is off in the test setup)", async () => {
  const key = status(await guest.post("/threads", {}), 200).json.key;
  equal(await wsProbe(`/listener/stream?threadKey=${key}`, guest.token), 503, "status");
});

test("the microphone socket needs a thread", async () => {
  equal(await wsProbe(`/listener/stream`, guest.token), 400, "status");
});

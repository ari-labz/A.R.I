import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

async function threadWithOneExchange(client = master) {
  const key = status(await client.post("/threads", {}), 200).json.key;
  await client.stream(`/threads/${key}/messages`, { prompt: "ping for the messages test" });
  return key;
}

test("a thread's messages come from /messages, and include what was said", async () => {
  const key = await threadWithOneExchange();
  const r = status(await master.get(`/threads/${key}/messages`), 200);
  assert(Array.isArray(r.json) && r.json.length >= 2, "should hold the question and the reply");
  assert(JSON.stringify(r.json).includes("ping for the messages test"), "should hold the question");
});

test("the thread itself says whether it is busy, and no longer carries the messages too", async () => {
  const key = await threadWithOneExchange();
  const r = status(await master.get(`/threads/${key}`), 200);
  has(r.json, "key", "state", "pipeline", "isInternal", "isProcessing", "lastMessageAt");
  equal(r.json.isProcessing, false);
  assert(!("history" in r.json), "messages should only come from /messages");
});

test("the old history, status, processing and stream routes are gone", async () => {
  const key = await threadWithOneExchange();
  status(await master.get(`/threads/${key}/history`), 404);
  status(await master.get(`/threads/${key}/status`), 404);
  status(await master.del(`/threads/${key}/processing`), 404);
  status(await master.post(`/threads/${key}/stream`, { prompt: "x" }), 404);
  status(await master.post(`/threads/${key}/interject`, { text: "x" }), 404);
});

test("an interjection into a thread that is not mid-reply is turned away", async () => {
  const key = await threadWithOneExchange();
  status(await master.post(`/threads/${key}/interjections`, { text: "wait" }), 409);
});

test("a guest cannot read the messages of someone else's thread", async () => {
  const key = await threadWithOneExchange(master);
  status(await guest.get(`/threads/${key}/messages`), 403);
  status(await guest.get(`/threads/${key}`), 403);
});

test("a guest reads their own thread's messages", async () => {
  const key = await threadWithOneExchange(guest);
  status(await guest.get(`/threads/${key}/messages`), 200);
});

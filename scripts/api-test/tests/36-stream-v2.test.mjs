import { test, status, has, equal, assert, base } from "../lib.mjs";
const { master, guest } = globalThis.ari;

async function readEvents(client, key, prompt, query = "?format=v2") {
  const res = await fetch(`${base}/api/v1/threads/${key}/messages${query}`, {
    method: "POST", headers: { Authorization: `Bearer ${client.token}`, "Content-Type": "application/json" }, body: JSON.stringify({ prompt }),
  });
  const text = await res.text();
  const events = [];
  for (const chunk of text.split("\n\n")) {
    if (!chunk.trim() || chunk.startsWith(":")) continue;
    const name = /^event: (.*)$/m.exec(chunk)?.[1], data = /^data: (.*)$/m.exec(chunk)?.[1], id = /^id: (.*)$/m.exec(chunk)?.[1];
    events.push({ name, id, data: data && query.includes("v2") ? JSON.parse(data) : null });
  }
  return { res, events, text };
}

test("the structured stream starts with message.start, carries only new text, and ends with the whole message", async () => {
  const key = status(await master.post("/threads", {}), 200).json.key;
  const { res, events } = await readEvents(master, key, "tell me something for the v2 stream");
  assert(res.headers.get("content-type").includes("event-stream"));
  equal(events[0].name, "message.start");
  equal(events[0].data.threadId, key);
  equal(events.at(-1).name, "message.done");
  const ids = events.map(e => Number(e.id));
  assert(ids.every((v, i) => i === 0 || v === ids[i - 1] + 1), "ids count up by one");

  // Rebuild the reply from the pieces and compare it with the finished message.
  const texts = {};
  for (const e of events) {
    if (e.name === "block.start" && e.data.block.type === "text") texts[e.data.index] = e.data.block.text;
    if (e.name === "text.delta") texts[e.data.index] += e.data.text;
    if (e.name === "block.update" && e.data.block.type === "text") texts[e.data.index] = e.data.block.text;
  }
  const done = events.at(-1).data.message;
  equal(done.type, "ariResponse");
  const finalText = done.blocks.filter(b => b.type === "text").map(b => b.text).join("");
  equal(Object.values(texts).join(""), finalText, "the pieces add up to the finished message");
  assert(finalText.length > 0, "there was a reply");
});

test("the finished message is exactly what the messages list holds", async () => {
  const key = status(await master.post("/threads", {}), 200).json.key;
  const { events } = await readEvents(master, key, "compare me");
  const done = events.at(-1).data.message;
  const list = status(await master.get(`/threads/${key}/messages`), 200).json;
  const last = list.filter(i => i.type === "ariResponse").at(-1);
  equal(JSON.stringify(done.blocks.filter(b => b.type === "text").map(b => b.text)), JSON.stringify(last.blocks.filter(b => b.type === "text").map(b => b.text)));
});

test("the original format is untouched: the whole reply so far, then [DONE]", async () => {
  const key = status(await master.post("/threads", {}), 200).json.key;
  const { text } = await readEvents(master, key, "old format please", "");
  assert(!text.includes("event: "), "no named events in the old format");
  assert(text.trimEnd().endsWith("data: [DONE]"), "ends with [DONE]");
});

test("problems before the reply starts are still reported, in the structured form", async () => {
  const key = status(await master.post("/threads", {}), 200).json.key;
  const { events } = await readEvents(master, key, "   ");
  assert(["message.done", "error"].includes(events.at(-1).name), "ended cleanly: " + events.at(-1).name);
});

test("a guest uses the structured stream on their own thread only", async () => {
  const mine = status(await guest.post("/threads", {}), 200).json.key;
  const ok = await readEvents(guest, mine, "guest v2");
  equal(ok.events.at(-1).name, "message.done");
  const theirs = status(await master.post("/threads", {}), 200).json.key;
  const refused = await readEvents(guest, theirs, "no", "?format=v2");
  equal(refused.res.status, 403);
});

import { test, status, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

async function threadWithProject(client, projectId) {
  const key = status(await client.post("/threads", { projectId }), 200).json.key;
  // The folder is attached to the thread the first time a message is sent.
  await client.stream(`/threads/${key}/stream`, { prompt: "hi" });
  return key;
}

test("a thread cannot read a file from a neighbouring project whose folder name starts the same", async () => {
  const mine   = status(await guest.post("/projects", { name: "Pfx" }), 200).json;
  const secret = status(await master.post("/projects", { name: "Pfx-secret" }), 200).json;
  status(await master.upload(`/projects/${secret.id}/files`, "secret.txt", "top secret"), 200);

  const key = await threadWithProject(guest, mine.id);
  const r = await guest.get(`/threads/${key}/file?path=../Pfx-secret/secret.txt`);
  assert(r.status === 400 || r.status === 403 || r.status === 404, `expected the read to be refused, got ${r.status} ${r.text.slice(0, 80)}`);
  assert(!r.text.includes("top secret"), "the secret must not be returned");
});

test("a thread can still download its own delivered file", async () => {
  const mine = status(await guest.post("/projects", { name: "OwnFiles" }), 200).json;
  status(await guest.upload(`/projects/${mine.id}/files`, "mine.txt", "my own"), 200);
  const key = await threadWithProject(guest, mine.id);
  const r = status(await guest.get(`/threads/${key}/file?path=mine.txt`), 200);
  equal(r.text, "my own");
});

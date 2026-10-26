import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;
const newThread = async (c = master) => status(await c.post("/threads", {}), 200).json.key;

test("a thread's files are uploaded, listed, fetched and removed at /files", async () => {
  const key = await newThread();
  status(await master.upload(`/threads/${key}/files`, "notes.txt", "remember the milk"), 200);
  const list = status(await master.get(`/threads/${key}/files`), 200).json;
  const mine = list.find(f => f.name === "notes.txt");
  assert(mine, "the upload should be listed");
  assert(mine.url.startsWith("/api/v1/threads/"), "the link it hands out should be a real address: " + mine.url);
  const got = status(await master.get(mine.url.replace("/api/v1", "")), 200);
  equal(got.text, "remember the milk");
  status(await master.del(`/threads/${key}/files?path=notes.txt`), 200);
  const after = status(await master.get(`/threads/${key}/files`), 200).json;
  assert(!after.some(f => f.name === "notes.txt"), "should be gone");
});

test("a picture comes back as a picture so it can be shown, anything else as a download", async () => {
  const key = await newThread();
  status(await master.upload(`/threads/${key}/files`, "dot.png", "not really a png", "image/png"), 200);
  status(await master.upload(`/threads/${key}/files`, "data.csv", "a,b", "text/csv"), 200);
  equal(status(await master.get(`/threads/${key}/files/content?path=dot.png`), 200).headers.get("content-type"), "image/png");
  assert((await master.get(`/threads/${key}/files/content?path=data.csv`)).headers.get("content-disposition")?.includes("attachment"), "should download");
});

test("removing a file cannot reach outside the thread's folder", async () => {
  const key = await newThread();
  status(await master.del(`/threads/${key}/files?path=../../../../etc/hosts`), 400);
  status(await master.del(`/threads/${key}/files`), 400);
});

test("files for a message are staged at /messageAttachments and can be taken back", async () => {
  const key = await newThread();
  const up = status(await master.upload(`/threads/${key}/messageAttachments`, "pic.txt", "for the next message"), 200).json;
  const list = status(await master.get(`/threads/${key}/messageAttachments`), 200).json;
  assert(list.length === 1, "one staged");
  status(await master.del(`/threads/${key}/messageAttachments/${list[0].id}`), 200);
  equal(status(await master.get(`/threads/${key}/messageAttachments`), 200).json.length, 0);
});

test("the old attachment, scratchpad, file and msg-attachment routes are gone", async () => {
  const key = await newThread();
  status(await master.get(`/threads/${key}/attachments`), 404);
  status(await master.get(`/threads/${key}/message-attachments`), 404);
  status(await master.get(`/threads/${key}/file?path=x`), 404);
  status(await master.get(`/threads/${key}/scratchpad/x.png`), 404);
  status(await master.get(`/threads/${key}/msg-attachment?id=x`), 404);
  status(await master.post(`/threads/${key}/promote-to-project`, {}), 404);
});

test("a guest cannot touch another person's thread files", async () => {
  const key = await newThread(master);
  status(await guest.get(`/threads/${key}/files`), 403);
  status(await guest.del(`/threads/${key}/files?path=a`), 403);
  status(await guest.get(`/threads/${key}/messageAttachments`), 403);
});

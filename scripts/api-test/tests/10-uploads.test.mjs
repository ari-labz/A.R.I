import fs from "node:fs";
import { test, status, equal, assert, makeZip, exists, scratchpad, dataDir } from "../lib.mjs";
const { master, guest } = globalThis.ari;

async function newThread(client = master) { return status(await client.post("/threads", {}), 200).json.key; }

test("uploading a file named '../../x' saves it under its plain name inside the thread folder", async () => {
  const key = await newThread();
  const r = status(await master.upload(`/threads/${key}/files`, "../../../escape-one.txt", "hello"), 200);
  equal(r.json.name, "escape-one.txt");
  assert(exists(`${scratchpad(key)}/escape-one.txt`), "file should be in the thread folder");
  assert(!exists(`${dataDir}/escape-one.txt`), "file must not escape to the data folder");
  assert(!exists(`${dataDir}/Server/escape-one.txt`), "file must not escape to the server folder");
});

test("uploading a file with a full path as its name keeps only the file name", async () => {
  const key = await newThread();
  const target = `/tmp/ari-escape-abs-${Date.now()}.txt`;
  const r = status(await master.upload(`/threads/${key}/files`, target, "hello"), 200);
  assert(r.json.name.startsWith("ari-escape-abs-"), "name should be the plain file name");
  assert(!exists(target), "the full path must not be written to");
});

test("a message attachment with a path in its name is also cleaned", async () => {
  const key = await newThread();
  const big = "x".repeat(12000);   // over 10,000 characters: saved to the thread folder
  const r = status(await master.upload(`/threads/${key}/messageAttachments`, "../../../escape-two.txt", big), 200);
  equal(r.json.name, "escape-two.txt");
  assert(exists(`${scratchpad(key)}/escape-two.txt`), "big text should be saved in the thread folder");
  assert(!exists(`${dataDir}/escape-two.txt`), "must not escape");
  const small = status(await master.upload(`/threads/${key}/messageAttachments`, "../../escape-three.png", "tiny", "image/png"), 200);
  equal(small.json.name, "escape-three.png");
  assert(!exists(`${dataDir}/Server/escape-three.png`) && !exists(`${dataDir}/escape-three.png`), "image must not escape");
});

test("a zip with an entry that climbs out of the folder is not unpacked outside it", async () => {
  const key = await newThread();
  const zip = makeZip({ "good/a.txt": "fine", "../../../zip-slip.txt": "bad" });
  const r = status(await master.upload(`/threads/${key}/files`, "pack.zip", zip, "application/zip"), 200);
  assert(r.json.extracted.includes("good/a.txt"), "the normal file should be unpacked");
  assert(!r.json.extracted.includes("../../../zip-slip.txt"), "the climbing entry must not be unpacked");
  assert(r.json.skipped.includes("../../../zip-slip.txt"), "the climbing entry should be reported as skipped");
  assert(exists(`${scratchpad(key)}/good/a.txt`), "normal file should exist");
  assert(!exists(`${dataDir}/zip-slip.txt`) && !exists(`${dataDir}/Server/zip-slip.txt`), "nothing may be written outside");
});

test("a guest's upload is also confined", async () => {
  const key = await newThread(guest);
  status(await guest.upload(`/threads/${key}/files`, "../../../guest-escape.txt", "hi"), 200);
  assert(!exists(`${dataDir}/guest-escape.txt`), "guest upload must not escape");
});

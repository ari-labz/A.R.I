import { test, status, equal } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("project sync refuses a commit id that is not a commit id", async () => {
  const p = status(await guest.post("/projects", { name: "Sync check" }), 200).json;
  for (const bad of ["--output=/tmp/x", "abc def", "HEAD", "../x", "a".repeat(41)]) {
    equal((await guest.get(`/projects/${p.id}/sync/status?clientSha=${encodeURIComponent(bad)}`)).status, 400, `status for "${bad}"`);
    equal((await guest.get(`/projects/${p.id}/sync/pull?clientSha=${encodeURIComponent(bad)}`)).status, 400, `pull status for "${bad}"`);
  }
});

test("project sync still works with a real commit id and with none", async () => {
  const p = status(await guest.post("/projects", { name: "Sync ok" }), 200).json;
  const s = status(await guest.get(`/projects/${p.id}/sync/status`), 200).json;
  status(await guest.get(`/projects/${p.id}/sync/status?clientSha=${s.serverSha}`), 200);
  status(await guest.get(`/projects/${p.id}/sync/status?clientSha=1234567890abcdef1234567890abcdef12345678`), 200);
});

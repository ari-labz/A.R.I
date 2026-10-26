import { test, status, has, equal, assert, Client } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

function isErrorShape(r, label) {
  assert(r.json && typeof r.json.error === "string" && r.json.error.length > 0, `${label}: expected {"error": "..."} but got ${r.status} ${r.text.slice(0, 80)}`);
}

test("every kind of failure answers { error: \"...\" }", async () => {
  const cases = [
    ["not signed in", anon.get("/threads"), 401],
    ["not allowed (guest on admin)", guest.get("/admin/users"), 403],
    ["not found (no such route)", master.get("/nothing/here"), 404],
    ["not found (no such thread route)", master.get("/threads/none/files/nothing"), 404],
    ["not found (no such entry)", master.get("/admin/calendar/entries/999999"), 404],
    ["a bad request with plain words", master.post("/admin/users", { username: "x", password: "y", role: "Guest" }), 400],
    ["a bad request, wrong JSON shape", master.post("/threads", "not json at all", { headers: { "Content-Type": "application/json" }, raw: "{oops" }), 400],
    ["a conflict", master.post("/admin/users", { username: "master", password: "long-enough-1", role: "Guest" }), 409],
  ];
  for (const [label, promise, expected] of cases) {
    const r = await promise;
    equal(r.status, expected, label);
    isErrorShape(r, label);
  }
});

test("a sign-in failure keeps its extra detail alongside the error", async () => {
  const r = await anon.post("/auth/login", { username: "nobody", password: "wrong" });
  equal(r.status, 401);
  isErrorShape(r, "login");
  assert("attemptsRemaining" in r.json, "attemptsRemaining should still be there");
});

test("a success with nothing to hand back is { ok: true }, never an empty body", async () => {
  const key = status(await master.post("/threads", {}), 200).json.key;
  const r = status(await master.post(`/threads/${key}/typing`, {}), 200);
  equal(r.json?.ok, true);
  const second = new Client(status(await anon.post("/auth/login", { username: "guest", password: "guest-password-1" }), 200).json.token);
  equal(status(await second.post("/auth/logout", {}), 200).json?.ok, true);
});

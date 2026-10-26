// Runs last: the first mode locks the test machine's own address out, so nothing can run after it.
import { test, status, equal, assert } from "../lib.mjs";
const { anon } = globalThis.ari;

const fail = (xff) => anon.post("/auth/login", { username: "master", password: "wrong-password" }, xff ? { headers: { "X-Forwarded-For": xff } } : {});

if (process.env.ARI_TEST_TRUST_PROXY === "1") {
  test("behind a trusted proxy, the lockout follows the address the proxy reports", async () => {
    equal((await fail("203.0.113.50")).status, 401, "1st");
    equal((await fail("203.0.113.50")).status, 401, "2nd");
    equal((await fail("203.0.113.50")).status, 403, "3rd locks that address");
    const again = await anon.post("/auth/login", { username: "master", password: "master-password-1" }, { headers: { "X-Forwarded-For": "203.0.113.50" } });
    equal(again.status, 403, "that address stays locked, even with the right password");
    status(await anon.post("/auth/login", { username: "master", password: "master-password-1" }, { headers: { "X-Forwarded-For": "203.0.113.51" } }), 200, "a different address is fine");
    status(await anon.post("/auth/login", { username: "master", password: "master-password-1" }), 200, "the proxy's own address is fine");
  });
} else {
  test("a forged X-Forwarded-For header cannot dodge the lockout", async () => {
    equal((await fail("198.51.100.1")).status, 401, "1st");
    equal((await fail("198.51.100.2")).status, 401, "2nd");
    equal((await fail("198.51.100.3")).status, 403, "3rd: counted against the real address, not the forged ones");
    equal((await anon.post("/auth/login", { username: "master", password: "master-password-1" }, { headers: { "X-Forwarded-For": "198.51.100.4" } })).status, 403,
      "a fresh forged address does not help once locked");
  });
}

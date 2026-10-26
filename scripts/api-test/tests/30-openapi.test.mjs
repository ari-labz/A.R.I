import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest, anon } = globalThis.ari;

const verbsOf = spec => Object.entries(spec.paths).flatMap(([path, verbs]) => Object.keys(verbs).map(verb => ({ path, verb, op: verbs[path] ?? verbs[verb] })));

test("the API describes itself at /openapi.json, for anyone signed in", async () => {
  status(await anon.get("/openapi.json"), 401);
  const spec = status(await guest.get("/openapi.json"), 200).json;
  equal(spec.openapi, "3.0.3");
  assert(Object.keys(spec.paths).length > 80, "should list the routes: " + Object.keys(spec.paths).length);
  assert(Object.keys(spec.paths).every(p => p.startsWith("/api/v1/")), "every path should be under /api/v1");
});

test("the description knows the shape of a body and the parts of an address", async () => {
  const spec = status(await master.get("/openapi.json"), 200).json;
  const patch = spec.paths["/api/v1/admin/settings"].patch;
  assert(patch.requestBody.content["application/json"].schema.properties.persona, "settings body lists persona");
  const thread = spec.paths["/api/v1/threads/{id}/messages"];
  assert(thread && thread.get && thread.post, "messages can be read and sent");
  assert(thread.get.parameters.some(p => p.name === "id" && p.in === "path"), "the address part is named id");
});

test("EVERY route under /admin refuses a guest, and EVERY other route refuses someone not signed in (except the few open ones)", async () => {
  const spec = status(await master.get("/openapi.json"), 200).json;
  const open = new Set(["/api/v1/auth/login", "/api/v1/theme", "/api/v1/branding", "/api/v1/setup/status", "/api/v1/setup/account", "/api/v1/health"]);
  let checked = 0;
  for (const [path, verbs] of Object.entries(spec.paths)) {
    if (path.includes("/client") || path.includes("/listener/stream")) continue;          // sockets are checked in their own tests
    const url = path.replace(/^\/api\/v1/, "").replace(/\{[^}]+\}/g, "1");
    for (const [verb, op] of Object.entries(verbs)) {
      const method = verb === "delete" ? "del" : verb;
      if (op["x-adminOnly"]) {
        const r = await guest[method](url, method === "get" ? undefined : {});
        equal(r.status, 403, `a guest on ${verb.toUpperCase()} ${path}`);
      }
      if (!open.has(path)) {
        const r = await anon[method](url, method === "get" ? undefined : {});
        equal(r.status, 401, `nobody signed in on ${verb.toUpperCase()} ${path}`);
      }
      checked++;
    }
  }
  assert(checked > 100, "checked " + checked);
});

test("no two routes answer the same verb at the same address", async () => {
  const spec = status(await master.get("/openapi.json"), 200).json;
  const ids = Object.values(spec.paths).flatMap(v => Object.values(v)).map(o => o.operationId);
  equal(new Set(ids).size, ids.length, "operation ids are unique");
});

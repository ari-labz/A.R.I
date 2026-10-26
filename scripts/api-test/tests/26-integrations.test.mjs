import { test, status, has, equal, assert } from "../lib.mjs";
const { master, guest } = globalThis.ari;

test("Discord and GitHub are grouped under /admin/integrations", async () => {
  has(status(await master.get("/admin/integrations/discord"), 200).json, "enabled", "tokenConfigured", "whitelistedUserIds", "watchedChannelIds", "allowedGuildIds");
  has(status(await master.get("/admin/integrations/github"), 200).json, "clientId", "connected", "commitAs");
});

test("Discord settings change with PATCH: what is sent changes, what is not sent stays", async () => {
  const before = status(await master.get("/admin/integrations/discord"), 200).json;
  status(await master.patch("/admin/integrations/discord", { whitelistedUserIds: ["111", "222"] }), 200);
  let now = status(await master.get("/admin/integrations/discord"), 200).json;
  equal(JSON.stringify(now.whitelistedUserIds), JSON.stringify(["111", "222"]));
  equal(now.masterDiscordId, before.masterDiscordId, "master untouched");
  equal(now.enabled, before.enabled, "enabled untouched");
  equal((await master.patch("/admin/integrations/discord", { whitelistedUserIds: ["abc"] })).status, 400, "non-numeric id");
  status(await master.patch("/admin/integrations/discord", { whitelistedUserIds: before.whitelistedUserIds }), 200);
});

test("GitHub settings change with PATCH, and the connection is its own thing", async () => {
  const before = status(await master.get("/admin/integrations/github"), 200).json;
  status(await master.patch("/admin/integrations/github", { commitAs: "CoAuthor" }), 200);
  let now = status(await master.get("/admin/integrations/github"), 200).json;
  equal(now.commitAs, "CoAuthor"); equal(now.clientId, before.clientId, "client id untouched");
  equal((await master.patch("/admin/integrations/github", { commitAs: "Nobody" })).status, 400, "bad value");
  equal((await master.patch("/admin/integrations/github", { clientId: " " })).status, 400, "blank client id");
  status(await master.patch("/admin/integrations/github", { commitAs: before.commitAs }), 200);
  status(await master.get("/admin/integrations/github/connection"), 200);
  status(await master.del("/admin/integrations/github/connection"), 200);
});

test("the old /admin/discord and /admin/github routes are gone", async () => {
  for (const path of ["/admin/discord/config", "/admin/github/config", "/admin/github/connect/status"])
    status(await master.get(path), 404, path);
  status(await master.post("/admin/github/disconnect", {}), 404);
});

test("a guest is kept out", async () => {
  status(await guest.get("/admin/integrations/discord"), 403);
  status(await guest.patch("/admin/integrations/github", { commitAs: "Author" }), 403);
});

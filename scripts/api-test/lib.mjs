// A tiny test toolkit for the API tests: no packages needed, just Node.
import fs from "node:fs";
import { execFileSync } from "node:child_process";
import http from "node:http";

export const base = process.env.ARI_TEST_URL ?? "http://localhost:5199";
export const dataDir = process.env.ARI_TEST_DATA ?? "";

/** Where the API lives. Tests use paths like "/threads"; this is put in front. */
export const prefix = "";

const tests = [];
export function test(name, fn) { tests.push({ name, fn }); }

export class Failure extends Error {}
export function assert(condition, message) { if (!condition) throw new Failure(message ?? "assertion failed"); }
export function equal(actual, expected, what = "value") {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a !== e) throw new Failure(`${what}: expected ${e} but got ${a}`);
}
export function status(res, expected, what = "") {
  if (res.status !== expected) {
    throw new Failure(`${what} ${res.method} ${res.path}: expected status ${expected} but got ${res.status}: ${String(res.text).slice(0, 300)}`);
  }
  return res;
}
export function has(obj, ...keys) {
  for (const k of keys) if (!obj || !(k in obj)) throw new Failure(`expected property "${k}" in ${JSON.stringify(obj)?.slice(0, 300)}`);
}
export const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Wait until check() returns something truthy (or give up after timeoutMs). */
export async function waitFor(check, what, timeoutMs = 20000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    const v = await check();
    if (v) return v;
    await sleep(200);
  }
  throw new Failure(`timed out waiting for ${what}`);
}

export class Client {
  constructor(token = null) { this.token = token; }

  async request(method, path, body, options = {}) {
    const headers = { ...(options.headers ?? {}) };
    if (this.token && !options.noAuth) headers["Authorization"] = `Bearer ${this.token}`;
    let payload = options.raw;
    if (body !== undefined && payload === undefined) {
      headers["Content-Type"] = headers["Content-Type"] ?? "application/json";
      payload = JSON.stringify(body);
    }
    const res = await fetch(base + prefix + path, { method, headers, body: payload, redirect: "manual" });
    const text = await res.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* not JSON */ }
    return { status: res.status, headers: res.headers, text, json, method, path };
  }
  get(path, options)        { return this.request("GET", path, undefined, options); }
  post(path, body, options) { return this.request("POST", path, body ?? {}, options); }
  put(path, body, options)  { return this.request("PUT", path, body ?? {}, options); }
  patch(path, body, options){ return this.request("PATCH", path, body ?? {}, options); }
  del(path, options)        { return this.request("DELETE", path, undefined, options); }

  /** POST a file as multipart form data. */
  async upload(path, fileName, content, type = "text/plain", query = "") {
    const form = new FormData();
    form.append("file", new Blob([content], { type }), fileName);
    const headers = {};
    if (this.token) headers["Authorization"] = `Bearer ${this.token}`;
    const res = await fetch(base + prefix + path + query, { method: "POST", headers, body: form });
    const text = await res.text();
    let json = null; try { json = JSON.parse(text); } catch { /* */ }
    return { status: res.status, headers: res.headers, text, json, method: "POST", path };
  }

  /** POST and read a server-sent-event stream to the end. Returns every "data:" line. */
  async stream(path, body, options = {}) {
    const headers = { "Content-Type": "application/json", ...(options.headers ?? {}) };
    if (this.token) headers["Authorization"] = `Bearer ${this.token}`;
    const res = await fetch(base + prefix + path, { method: options.method ?? "POST", headers, body: JSON.stringify(body ?? {}) });
    const events = [];
    if (res.headers.get("content-type")?.includes("event-stream")) {
      const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = "";
      for (;;) {
        const { done, value } = await reader.read(); if (done) break;
        buf += dec.decode(value, { stream: true });
        let i; while ((i = buf.indexOf("\n\n")) >= 0) { const block = buf.slice(0, i); buf = buf.slice(i + 2); events.push(block); }
        if (options.stopAfter && events.length >= options.stopAfter) { await reader.cancel(); break; }
      }
      return { status: res.status, events, path, method: "POST" };
    }
    const text = await res.text();
    return { status: res.status, events, text, path, method: "POST" };
  }
}

export async function runAll() {
  let passed = 0, failed = 0;
  const only = process.env.ARI_TEST_FILTER;
  for (const t of tests) {
    if (only && !t.name.includes(only)) continue;
    try { await t.fn(); passed++; console.log(`  ok    ${t.name}`); }
    catch (e) { failed++; console.log(`  FAIL  ${t.name}\n        ${e.message}`); if (!(e instanceof Failure)) console.log(e.stack); }
  }
  console.log(`\n${passed} passed, ${failed} failed`);
  return failed === 0;
}

/** The one-time Setup Token is only in the server log. */
export function readSetupToken() {
  const dir = `${dataDir}/Server/Logs/SystemLogs`;
  for (const f of fs.readdirSync(dir).sort().reverse()) {
    const m = fs.readFileSync(`${dir}/${f}`, "utf8").match(/Setup Token:\s*\n\s*\n\s*([A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4})/);
    if (m) return m[1];
  }
  throw new Failure("Setup Token not found in the log");
}

/** Build a zip file in memory. entries: { "name/inside.zip": "text" }. Names are used exactly as given, even "../x". */
export function makeZip(entries) {
  const script = "import sys,json,zipfile,io\nb=io.BytesIO()\nz=zipfile.ZipFile(b,'w')\nfor n,t in json.load(sys.stdin).items(): z.writestr(zipfile.ZipInfo(n),t)\nz.close()\nsys.stdout.buffer.write(b.getvalue())";
  return execFileSync("python3", ["-c", script], { input: JSON.stringify(entries), maxBuffer: 50 * 1024 * 1024 });
}

export const exists = p => fs.existsSync(p);
export const scratchpad = key => `${dataDir}/Server/Scratchpad/${key}`;

/** Try to open a WebSocket and report how the server answered: the status code, or 101 if it was accepted. */
export function wsProbe(path, token) {
  return new Promise(resolve => {
    const url = new URL(base + prefix + path);
    const req = http.request({
      host: url.hostname, port: url.port, path: url.pathname + url.search, method: "GET",
      headers: { Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13",
                 "Sec-WebSocket-Key": Buffer.from("0123456789abcdef").toString("base64"),
                 ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    });
    req.on("response", res => { res.resume(); resolve(res.statusCode); });
    req.on("upgrade", (res, socket) => { socket.destroy(); resolve(101); });
    req.on("error", () => resolve(0));
    req.end();
  });
}

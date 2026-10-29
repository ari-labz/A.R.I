"""Runs the tasks in tasks.py against a live ARI. See README.md."""
import json, os, sys, time, threading, zipfile, re, warnings, subprocess
import requests
warnings.filterwarnings("ignore")

BASE = os.environ.get("ARI_URL", "http://localhost:5074") + "/api/v1"
DATA = os.environ.get("ARI_DATA", os.path.expanduser("~/ARI/Server"))
s = requests.Session()


def login():
    user, pw = os.environ.get("ARI_BENCH_USER"), os.environ.get("ARI_BENCH_PASS")
    if not user or not pw: sys.exit("Set ARI_BENCH_USER and ARI_BENCH_PASS (a test account on a dev-mode ARI).")
    r = s.post(BASE + "/auth/login", json={"username": user, "password": pw}); r.raise_for_status()
    s.headers["Authorization"] = "Bearer " + r.json()["token"]


DESTRUCTIVE = ("rm ", "sudo", "curl", "wget", "mkfs", "dd ", "> /dev", "chmod -R")


class Run:
    """One conversation: sends messages, answers approval prompts, and keeps what happened."""
    def __init__(self):
        self.key = s.post(BASE + "/threads", json={}).json()["key"]
        self.folder = os.path.join(DATA, "Scratchpad", self.key)
        self.approvals = []; self.uploaded = {}
        threading.Thread(target=self._watch, daemon=True).start()

    def _watch(self):
        try:
            with s.get(f"{BASE}/threads/{self.key}/watch", stream=True, timeout=3600) as r:
                for line in r.iter_lines(decode_unicode=True):
                    if not line or not line.startswith("data:"): continue
                    try: a = json.loads(line[5:]).get("approval")
                    except Exception: continue
                    if a:
                        ok = not any(b in a.get("command", "") for b in DESTRUCTIVE)
                        self.approvals.append((a.get("command", ""), ok))
                        s.post(f"{BASE}/threads/{self.key}/approvals/{a['id']}", json={"allow": ok})
        except Exception: pass

    def upload(self, name, content):
        data = content if isinstance(content, bytes) else content.encode()
        self.uploaded[name] = data
        r = s.post(f"{BASE}/threads/{self.key}/files", files={"file": (name, data, "application/octet-stream")}); r.raise_for_status()

    def say(self, text, timeout=1200):
        t = time.time()
        with s.post(f"{BASE}/threads/{self.key}/messages", json={"Prompt": text}, stream=True, timeout=timeout) as r:
            r.encoding = "utf-8"
            for line in r.iter_lines(decode_unicode=True):
                if line and line.strip() == "data: [DONE]": break
        secs = round(time.time() - t, 1)
        m = s.get(f"{BASE}/threads/{self.key}/messages?raw=true").json(); m = m if isinstance(m, list) else m.get("messages", [])
        last_user = max(i for i, x in enumerate(m) if x.get("type") == "userMessage")
        blocks = [b for x in m[last_user + 1:] if x.get("type") == "ariResponse" for b in x.get("blocks", [])]
        reply = "\n".join(b.get("text", "") for b in blocks if b.get("type") == "text" and b.get("text", "").strip())
        self.last_secs = secs
        return reply, secs

    def same(self, name):
        """True if an uploaded file is exactly as it was uploaded."""
        return self.exists(name) and self.read(name) == self.uploaded[name]

    def file(self, name): return os.path.join(self.folder, name)
    def exists(self, name): return os.path.exists(self.file(name))
    def read(self, name): return open(self.file(name), "rb").read()

    def tool_calls(self):
        """Tool names she called, from the day's DTI record for this thread."""
        names = []
        for day in sorted(os.listdir(os.path.join(DATA, "Logs", "DTILogs")))[-2:]:
            f = os.path.join(DATA, "Logs", "DTILogs", day, self.key + ".jsonl")
            if os.path.exists(f):
                for l in open(f):
                    try: d = json.loads(l)
                    except Exception: continue
                    if d.get("event") == "tool_call": names.append(d.get("name"))
        return names


def office_text(path):
    """All the text in a docx/pptx/xlsx (xml tags stripped), for checks."""
    z = zipfile.ZipFile(path); out = []
    for n in z.namelist():
        if n.endswith(".xml") and (n.startswith(("word/", "ppt/slides/", "xl/sharedStrings", "xl/worksheets/"))):
            out.append(" ".join(re.findall(r">([^<>]+)<", z.read(n).decode("utf8", "ignore"))))
    return " ".join(out)


def main():
    from tasks import TASKS
    wanted = sys.argv[1:]
    login()
    results = []
    for task in TASKS:
        if wanted and not any(w in task["id"] for w in wanted): continue
        print(f"\n=== {task['id']}: {task['title']}", flush=True)
        run = Run(); ctx = {"run": run, "replies": [], "secs": []}
        try:
            for name, content in task.get("files", {}).items(): run.upload(name, content() if callable(content) else content)
            for msg in task["messages"]:
                reply, secs = run.say(msg); ctx["replies"].append(reply); ctx["secs"].append(secs)
                print(f"  [{secs}s] {reply[:140].replace(chr(10), ' ')}", flush=True)
            if "after" in task: task["after"](ctx)
            checks = task["check"](ctx)
        except Exception as e:
            checks = [(f"ran without error ({e})", False)]
        calls = run.tool_calls()
        passed = sum(ok for _, ok in checks)
        for what, ok in checks: print(f"  {'PASS' if ok else 'FAIL'}  {what}", flush=True)
        results.append({"id": task["id"], "title": task["title"], "passed": passed, "total": len(checks), "seconds": round(sum(ctx["secs"]), 1),
                        "tool_calls": len(calls), "tools": sorted(set(c for c in calls if c)), "checks": [{"what": w, "ok": bool(o)} for w, o in checks],
                        "replies": ctx["replies"]})
    print("\n" + "-" * 78)
    for r in results: print(f"{r['id']:<14} {r['passed']}/{r['total']}  {r['seconds']:>7}s  {r['tool_calls']:>3} tool calls  {r['title']}")
    tp = sum(r["passed"] for r in results); tt = sum(r["total"] for r in results)
    print(f"\nTotal: {tp}/{tt} checks passed, {round(sum(r['seconds'] for r in results))}s")
    os.makedirs("results", exist_ok=True)
    out = os.path.join("results", time.strftime("%Y%m%d-%H%M%S") + ".json")
    json.dump(results, open(out, "w"), indent=1); print("Saved", out)


if __name__ == "__main__":
    main()

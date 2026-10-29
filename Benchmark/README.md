# ARI benchmark

A repeatable set of real jobs run against a live ARI, so "is she getting smarter or dumber?" has numbers. Each task sends one or two messages, then checks what actually happened on disk and in her reply (files made, originals untouched, figures right, processes stopped). It also records seconds taken and tool calls.

Run ARI through ARI.Console with `ARI_DEVMODE=1`, make a test account, then:

```bash
ARI_BENCH_USER=tester ARI_BENCH_PASS=... python3 bench.py            # everything
ARI_BENCH_USER=tester ARI_BENCH_PASS=... python3 bench.py sheet cv   # just tasks whose id contains one of these
```

Results print as a table and are saved to `results/<timestamp>.json`. Shell approvals are answered automatically (anything destructive is denied), so run it only against a dev instance.

Tasks live in `tasks.py`. Add one by giving it an id, the files to upload, the messages, and a `check(ctx)` that returns a list of `(what, ok)` pairs.

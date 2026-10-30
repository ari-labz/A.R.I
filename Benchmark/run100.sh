#!/bin/bash
# ARI_BENCH_USER=... ARI_BENCH_PASS=... ./run100.sh [id fragments]
cd "$(dirname "$0")" && BENCH_TASKS=tasks_100 python3 -W ignore bench.py "$@"

# syntax=docker/dockerfile:1.7
#
# A·R·I server image. One container runs everything: ARI.Core (API, web UI, Discord, scheduler) and the
# llama-server processes it starts itself, so no Electron console and no separate model server.
#
#   docker build -t ari .                       # for the machine you are building on
#   docker buildx build --platform linux/arm64 .   # DGX Spark / any ARM64 + NVIDIA box
#
# Data lives under /data (configs, brain, models, keys). Mount a volume or folder there to keep it across
# container replacement; mount nothing and it disappears with the container. See docker-compose.yml.

ARG CUDA_VERSION=13.0.1
# Pinned so builds are reproducible. Bump deliberately; ARI's own "llama.cpp update available" check is
# irrelevant here because the binary is baked in.
ARG LLAMA_CPP_REF=b11696
ARG DOTNET_VERSION=10.0


# ── 1. llama.cpp with CUDA ───────────────────────────────────────────────────────────────────────────
# ARI's built-in downloader only has Vulkan builds for Linux, and none that target a DGX Spark's GPU
# (GB10, compute capability 12.1). ARI looks for llama-server on PATH before downloading anything, so
# shipping our own build means it never tries.
FROM nvidia/cuda:${CUDA_VERSION}-devel-ubuntu24.04 AS llama
ARG LLAMA_CPP_REF
ARG TARGETARCH
# Space-separated override of the GPU architectures to compile for. Empty = pick by platform:
#   arm64 → 121 (GB10 / DGX Spark)      amd64 → Turing, Ampere, Ada and Blackwell desktop/datacentre
ARG CUDA_ARCHS=""
# Parallel compile jobs; empty = all cores. Lower it if the build host runs out of memory (CUDA kernels are heavy).
ARG LLAMA_JOBS=""

RUN apt-get update && apt-get install -y --no-install-recommends \
        git cmake build-essential ca-certificates libcurl4-openssl-dev \
    && rm -rf /var/lib/apt/lists/*

RUN git clone --depth 1 --branch "${LLAMA_CPP_REF}" https://github.com/ggml-org/llama.cpp /src/llama.cpp

RUN set -eux; \
    archs="${CUDA_ARCHS:-}"; \
    if [ -z "$archs" ]; then \
        case "${TARGETARCH:-$(dpkg --print-architecture)}" in \
            arm64) archs="121" ;; \
            *)     archs="75;80;86;89;120" ;; \
        esac; \
    fi; \
    # The CUDA toolkit ships a link-time stub of libcuda; the real one comes from the host driver at run time.
    # The stub is named libcuda.so, but libggml-cuda records its dependency as libcuda.so.1, so the linker needs
    # both the alias and a path to look in when it links the executables.
    ln -sf /usr/local/cuda/lib64/stubs/libcuda.so /usr/local/cuda/lib64/stubs/libcuda.so.1; \
    export LIBRARY_PATH="/usr/local/cuda/lib64/stubs:${LIBRARY_PATH:-}"; \
    cmake -S /src/llama.cpp -B /build \
        -DCMAKE_BUILD_TYPE=Release \
        -DGGML_CUDA=ON \
        -DCMAKE_CUDA_ARCHITECTURES="$archs" \
        -DGGML_NATIVE=OFF \
        -DCMAKE_EXE_LINKER_FLAGS="-Wl,-rpath-link,/usr/local/cuda/lib64/stubs" \
        -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-rpath-link,/usr/local/cuda/lib64/stubs" \
        -DLLAMA_CURL=ON \
        -DLLAMA_BUILD_TESTS=OFF \
        -DLLAMA_BUILD_EXAMPLES=OFF \
        -DLLAMA_BUILD_SERVER=ON; \
    cmake --build /build --config Release -j"${LLAMA_JOBS:-$(nproc)}" --target llama-server llama-cli llama-bench; \
    mkdir -p /opt/llama.cpp/bin; \
    cp /build/bin/llama-server /build/bin/llama-cli /build/bin/llama-bench /opt/llama.cpp/bin/; \
    cp -a /build/bin/*.so* /opt/llama.cpp/bin/ 2>/dev/null || true


# ── 2. ARI.Core + the web UI ─────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG TARGETARCH

# The csproj's BuildUI target runs bun from $HOME/.bun/bin.
COPY --from=oven/bun:1 /usr/local/bin/bun /root/.bun/bin/bun

WORKDIR /src
COPY . .

RUN set -eux; \
    case "${TARGETARCH:-$(dpkg --print-architecture)}" in \
        arm64) rid=linux-arm64 ;; \
        *)     rid=linux-x64 ;; \
    esac; \
    (cd ARI.Orb && /root/.bun/bin/bun install); \
    (cd ARI.UI  && /root/.bun/bin/bun install); \
    dotnet publish ARI.Core/ARI.Core.csproj -c Release -r "$rid" --self-contained true \
        -p:AppInstallRoot=/tmp/build -o /out; \
    # Same reason build-release.js does this: the publish glob is evaluated before the UI exists.
    mkdir -p /out/wwwroot; \
    cp -R ARI.API/wwwroot/. /out/wwwroot/; \
    test -f /out/wwwroot/index.html


# ── 3. Runtime ───────────────────────────────────────────────────────────────────────────────────────
FROM nvidia/cuda:${CUDA_VERSION}-runtime-ubuntu24.04 AS runtime

ARG ARI_VERSION=dev
LABEL org.opencontainers.image.title="A·R·I" \
      org.opencontainers.image.description="A·R·I server with llama.cpp (CUDA) and the web UI" \
      org.opencontainers.image.source="https://github.com/ari-labz/A.R.I" \
      org.opencontainers.image.version="${ARI_VERSION}"

# ca-certificates/curl: model downloads and the health check. git: ARI's git tools. espeak-ng + python:
# the voice and listener helpers ARI sets up on demand. libicu/libssl/libgomp/libcurl: runtime needs of
# the self-contained .NET app and llama-server. tini: reaps the llama-server children and forwards signals.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl git tini tzdata \
        libicu74 libssl3t64 libgomp1 libcurl4t64 \
        espeak-ng python3 python3-venv python3-pip \
    && rm -rf /var/lib/apt/lists/*

COPY --from=llama /opt/llama.cpp /opt/llama.cpp
COPY --from=build /out /opt/ari
# Some asset files are checked out owner-only; the app runs as an unprivileged user and must be able to read everything.
RUN chmod -R a+rX /opt/ari /opt/llama.cpp
COPY docker/entrypoint.sh /usr/local/bin/ari-entrypoint

# Dedicated user; the entrypoint fixes ownership of /data and then drops to PUID/PGID (default below).
RUN groupadd --gid 10001 ari \
    && useradd --uid 10001 --gid ari --home-dir /data --shell /usr/sbin/nologin ari \
    && chmod +x /usr/local/bin/ari-entrypoint /opt/ari/ARI.Core \
    && mkdir -p /data /opt/ari/runtimes \
    # ARI fetches the Discord voice library into <install>/runtimes on first start.
    && chown ari:ari /data /opt/ari/runtimes

# Container defaults: the voice, listener and image-generation helpers install large Python stacks on first start,
# so they are off until their own services exist (see docker-compose.yml). Everything else keeps its normal default.
RUN python3 - <<'PY'
import json
p = "/opt/ari/AppDataDefaults/AriConfig.json"
c = json.load(open(p))
for name in ("VoiceSynthesis", "Voice", "Listener", "ImageGen"):
    c["Modules"][name]["Enabled"] = False
json.dump(c, open(p, "w"), indent=2)
PY

# LD_LIBRARY_PATH: the llama.cpp binaries were linked in another directory and find their .so files by absolute rpath.
ENV PATH="/opt/llama.cpp/bin:${PATH}" \
    LD_LIBRARY_PATH=/opt/llama.cpp/bin \
    APP_INSTALL_ROOT=/opt/ari \
    APP_DATA_ROOT=/data \
    MODELS_PATH=/data/Models \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0 \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility

# No VOLUME instruction on purpose: an image-declared volume would keep data after the stack is deleted.
# The compose file decides whether /data is a named volume, a bind mount, or throwaway container storage.
EXPOSE 5074

HEALTHCHECK --interval=30s --timeout=5s --start-period=90s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5074/api/theme >/dev/null || exit 1

# Logs go to stdout (Serilog console sink), so `docker logs` is the log. The first-run Setup Token is in there too.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/ari-entrypoint"]
CMD ["/opt/ari/ARI.Core"]

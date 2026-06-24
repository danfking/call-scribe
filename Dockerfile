# syntax=docker/dockerfile:1

# call-scribe offline pipeline image. Runs everything downstream of a recorded WAV pair:
# transcribe, offline diarization + speaker embedding, the coach and its memory store. Live host
# capture is Windows-only (WASAPI loopback + the COM AEC DSP) and is NOT in this image; the build
# targets the portable net10.0 framework, where those commands report capture as unavailable.
#
# Multi-arch via buildx:
#   docker buildx build --platform linux/amd64,linux/arm64 -t call-scribe:latest .

ARG DOTNET_VERSION=10.0

# ---- build (runs on the native builder arch; cross-publishes for the target arch) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

# Map Docker's TARGETARCH to a .NET RID so the publish includes only the target's native assets
# (whisper.cpp + onnxruntime/sherpa) rather than every runtime.
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64   > /rid ;; \
      arm64) echo linux-arm64 > /rid ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac

# Restore first so the layer caches unless the project file or build props change. Pin to the
# portable net10.0 TFM (-p:TargetFrameworks): the net10.0-windows TFM cannot restore on Linux (it
# needs the Windows targeting packs) and is never published here anyway.
COPY Directory.Build.props ./
COPY src/CallScribe/CallScribe.csproj src/CallScribe/
RUN dotnet restore src/CallScribe/CallScribe.csproj -r "$(cat /rid)" -p:TargetFrameworks=net${DOTNET_VERSION}

# Publish the portable build, framework-dependent: the runtime base image carries the .NET runtime,
# and the RID pulls in the linux native libraries for Whisper.net and sherpa-onnx.
COPY src/ src/
RUN dotnet publish src/CallScribe/CallScribe.csproj \
      -f net${DOTNET_VERSION} -c Release -p:TargetFrameworks=net${DOTNET_VERSION} \
      -r "$(cat /rid)" --self-contained false --no-restore \
      -o /app

# ---- runtime (glibc Debian base; whisper/sherpa natives need glibc, not Alpine musl) ----
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime

# whisper.cpp and onnxruntime native libraries want OpenMP at runtime.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Unify config, models and output under one mountable home. On Linux .NET maps UserProfile -> $HOME,
# ApplicationData -> $HOME/.config and LocalApplicationData -> $HOME/.local/share, so with HOME=/data
# everything call-scribe reads/writes lands under a single /data volume:
#   /data/.config/call-scribe/config.json          settings (Ollama URL, Postgres conn)
#   /data/.local/share/call-scribe/models          whisper + speaker ONNX models
#   /data/call-scribe/recordings, /transcripts      the WAV pairs in, the .md transcripts out
ENV HOME=/data
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "/app/call-scribe.dll"]

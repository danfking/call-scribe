# call-scribe in Docker

A cross-platform (linux/amd64 + linux/arm64) image for the **offline** half of call-scribe. Live audio capture stays on Windows; the container does everything after a recording exists.

## What the container can and cannot do

**Can:**
- `transcribe` an existing `.others.wav` / `.me.wav` pair into the merged markdown transcript.
- Offline diarization and speaker attribution (sherpa-onnx).
- The coach (`coach`) and its TimescaleDB + pgvector memory store, via Ollama over HTTP.
- `config` to inspect or change settings.

**Cannot** (these report "live audio capture is only available on Windows" and exit):
- `record`, `start` (WASAPI loopback and microphone capture have no portable API).
- `devices` (MMDevice enumeration is Windows-only).
- `coach enroll-me` (records from the live mic; enroll from an existing WAV instead).

The intended split: a Windows host produces the WAV pair, then any machine transcribes and coaches it.

## Layout under `/data`

The image sets `HOME=/data`. On Linux .NET maps `UserProfile` to `$HOME` and `LocalApplicationData` to `$XDG_DATA_HOME` (and `AppConfig.ConfigPath` falls back to `LocalApplicationData` because `ApplicationData` is empty there), so call-scribe reads and writes under:

| Path | Holds |
| --- | --- |
| `/data/.local/share/call-scribe/config.json` | settings (Ollama URL, Postgres connection) |
| `/data/.local/share/call-scribe/models` | whisper + speaker ONNX models |
| `/data/call-scribe/recordings` | the `.others.wav` / `.me.wav` pairs you drop in |
| `/data/call-scribe/transcripts` | the `.md` transcripts produced |

The compose file mounts host `./data` (next to the compose file, i.e. `docker/data`) at `/data/call-scribe`, so your recordings live in `docker/data/recordings` and transcripts appear in `docker/data/transcripts`. It also mounts `callscribe.config.json` read-only at the config path and keeps a named volume for the downloaded models.

Note the user files mount at the `/data/call-scribe` subpath, not over `/data` itself: a host bind mount over `$HOME` breaks .NET's home-directory resolution in the container (the model and config paths then collapse onto the working directory). Whisper models download automatically on first transcription; the speaker ONNX models must be placed under the models directory yourself.

## Build

```
# native arch, loaded into the local daemon
docker build -t call-scribe:local .

# multi-arch (needs buildx + QEMU for the non-native arch)
docker buildx build --platform linux/amd64,linux/arm64 -t call-scribe:latest .
```

The build publishes the portable `net10.0` target framework-dependent for the target arch, so only that architecture's whisper/sherpa native libraries are included.

## Run

```
cd docker

# bring up the memory store and Ollama once
docker compose -f callscribe.compose.yml up -d coach-db ollama

# first run only: pull the coach models into the ollama volume
docker compose -f callscribe.compose.yml exec ollama ollama pull qwen3:4b
docker compose -f callscribe.compose.yml exec ollama ollama pull nomic-embed-text

# drop a recording pair into ./data/call-scribe/recordings, then:
docker compose -f callscribe.compose.yml run --rm app transcribe
docker compose -f callscribe.compose.yml run --rm app config
```

The `app` service is in the `app` compose profile and has no default command: it is a CLI run on demand with `run --rm app <verb>`, not a long-lived service.

## Notes and unverified edges

- The image uses a glibc Debian base (`mcr.microsoft.com/dotnet/runtime`), not Alpine: the whisper and onnxruntime native libraries link against glibc.
- `libgomp1` is installed for the OpenMP runtime those natives expect.
- linux/arm64: the image builds under `buildx`, the arm64 native libraries are present (Whisper.net.Runtime ships `runtimes/linux-arm64/libwhisper.so` plus the ggml libs; sherpa-onnx ships the arm64 `libonnxruntime.so` and `libsherpa-onnx-c-api.so`), and the .NET app runs under QEMU emulation (`config` works). Full transcription was verified only on linux/amd64; on arm64 it was not run to completion here because QEMU emulation is too slow, not because of any architecture problem. Expect it to work on real arm64 hardware (e.g. Apple Silicon); if a native fails to load there, that is the first thing to check.

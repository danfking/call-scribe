# call-scribe in Docker

A cross-platform (linux/amd64 + linux/arm64) image for the **offline** half of call-scribe. Live audio capture stays on Windows; the container does everything after a recording exists.

## What the container can and cannot do

**Can:**
- `transcribe` an existing `.others.wav` / `.me.wav` pair into the merged markdown transcript.
- Offline diarization and speaker attribution (sherpa-onnx).
- The coach (`coach`) and its TimescaleDB + pgvector memory store, via Ollama over HTTP.
- `config` to inspect or change settings.

**Cannot** (these report "live audio capture is only available on Windows" and exit):
- `record`, `listen` (WASAPI loopback and microphone capture have no portable API).
- `devices` (MMDevice enumeration is Windows-only).
- `coach enroll-me` (records from the live mic; enroll from an existing WAV instead).

The intended split: a Windows host produces the WAV pair, then any machine transcribes and coaches it.

## Layout under `/data`

The image sets `HOME=/data`, so one mounted volume holds everything call-scribe reads and writes:

| Path | Holds |
| --- | --- |
| `/data/.config/call-scribe/config.json` | settings (Ollama URL, Postgres connection) |
| `/data/.local/share/call-scribe/models` | whisper + speaker ONNX models |
| `/data/call-scribe/recordings` | the `.others.wav` / `.me.wav` pairs you drop in |
| `/data/call-scribe/transcripts` | the `.md` transcripts produced |

The compose file bind-mounts `./data` (next to the compose file, i.e. `docker/data`) to `/data` and overlays `callscribe.config.json` read-only at the config path. Whisper models download automatically on first transcription; the speaker ONNX models must be placed under the models directory yourself.

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
- linux/arm64 native presence for Whisper.net.Runtime 1.9.1 and sherpa-onnx 1.13.3 is expected per their published runtime assets but has not been exercised on real arm64 hardware here; if a native fails to load on arm64, that is the first thing to check.

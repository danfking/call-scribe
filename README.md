# call-scribe

Local, client-agnostic call transcription for Windows. Records any video call (Teams, Meet, Zoom, Discord, anything) and produces a markdown transcript that separates what you said from what everyone else said. Nothing leaves your machine.

## How it works

Instead of integrating with meeting clients, call-scribe works at the audio layer. Every call ends up as two audio streams on your PC: what comes out of your speakers (everyone else) and what goes into your microphone (you). call-scribe captures both as separate tracks via WASAPI, transcribes them locally with Whisper, and interleaves the results chronologically. Because the tracks are physically separate, speaker attribution between you and the other side is exact: no diarisation model, no guessing.

```
**Me** [13:26:00]
The point is it works at the audio layer, so it doesn't matter which client the call is on.

**Others** [13:26:14]
Right, so it has to actually listen to the output device.
```

## Usage

```
call-scribe record --label standup     # record until Enter, then auto-transcribe
call-scribe listen --label standup     # same, plus live captions on screen as the call happens
call-scribe record start --label sync  # detached recording
call-scribe record stop                # stop and finalise
call-scribe record status              # is a recording running?
call-scribe transcribe latest          # transcribe the newest recording
call-scribe devices                    # list audio devices
call-scribe config                     # show settings
```

`listen` shows fast-preview captions from a small model (small.en by default) while recording; the accurate transcript is still produced by the full model when you stop.

Transcripts land in `%USERPROFILE%\call-scribe\transcripts\` as markdown with wall-clock timestamps and YAML frontmatter.

## Requirements

- Windows 10/11 x64. No other installs: the exe is self-contained, capture is native WASAPI.
- The Whisper model (~874 MB) downloads automatically on first transcription.
- GPU acceleration (optional): download the CUDA build and have the NVIDIA CUDA Toolkit (12.4+) installed. The CPU build transcribes a one-hour call in a few minutes on a modern CPU; the CUDA build does it in seconds. The CUDA build falls back to CPU automatically if no usable GPU is found.

## Running the offline pipeline in Docker (Linux/macOS)

Live capture is Windows-only (WASAPI loopback and the COM echo-cancellation DSP have no portable equivalent), but everything downstream of a recording is platform-neutral. A multi-arch image (linux/amd64 + linux/arm64) runs the offline half: `transcribe`, offline diarization and speaker attribution, and the coach with its memory store. The split is: record on a Windows host, then transcribe and coach anywhere.

```
docker compose -f docker/callscribe.compose.yml up -d coach-db ollama
docker compose -f docker/callscribe.compose.yml run --rm app transcribe
```

The container does transcribe / diarize / coach / memory; it cannot do live host capture (`record`, `listen`, `devices`, `coach enroll-me` report that capture is Windows-only). See [docker/README.md](docker/README.md) for the full setup.

## The sharp edges

- The loopback capture records the **default output device**. If you take calls on a headset, make the headset your Windows default output before recording, and don't switch output devices mid-call.
- If you use mic-monitoring software that routes your microphone into your output mix, your own voice will bleed into the "Others" track; route the monitor mix away from the default output.
- Loopback level scales with your **output volume**. Normal listening levels are fine; if transcripts come back nearly empty, check that your volume isn't sitting near zero.
- On speakers (rather than headphones), the other side of the call is audible to your microphone, so their words will also appear in your "Me" track. Headphones give clean separation.

## Recording consent

You are recording conversations. Consent requirements vary by jurisdiction (some require all parties to consent). Know your local law and your workplace's policy before recording.

## Licence

MIT

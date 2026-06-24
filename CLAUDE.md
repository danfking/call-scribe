# call-scribe

A Windows .NET 10 CLI that records a call as two time-aligned tracks (system output = "Others",
microphone = "Me"), transcribes them with Whisper, and layers an optional local-LLM meeting "coach"
and voiceprint speaker-ID. Single assembly `call-scribe`; solution `CallScribe.slnx`.

## Build and test

```
dotnet build CallScribe.slnx                    # whole solution
dotnet test tests/CallScribe.Tests              # all unit tests
dotnet test tests/CallScribe.Tests --filter "FullyQualifiedName~SpeakerResolverTests"  # one class
```

- `Directory.Build.props` sets `TreatWarningsAsErrors=true` for every project, so any warning
  (including the platform-compatibility analyzer over the WASAPI/COM audio code) fails the build.
- Everything targets `net10.0-windows`. The CPU build is the headline; CUDA is opt-in only via the
  `CallScribeCuda=true` publish property (it balloons the artifact).

## Architecture

Namespace dependency direction (arrows = "depends on"):

```
Commands -> Audio, Transcription, Coach
Coach    -> Transcription   (for the LiveCaptionEngine.OthersLabel / MeLabel constants, CaptionEvent)
Audio, Transcription do NOT depend on Coach.
```

- **Audio** (`CaptureEngine`, `CaptureTrack`): two WASAPI captures (loopback + mic) into two WAVs.
  The two-track split is what gives exact speaker separation, the near side is never diarized.
- **Transcription**: live captions (`LiveCaptionEngine` + `LiveStatusDisplay` dashboard, small model,
  fixed audio-window chunking) vs the stop-time batch pass (`TranscriptionService`, large model + VAD,
  `TranscriptMerger` writes the `.md`).
- **Coach** (`CoachEngine`, `LlmAdvisor`/`StubAdvisor`, `Memory/`, `Speaker/`): watches the same caption
  stream, persists the live transcript to Postgres, and advises via Ollama. `Speaker/` resolves far-side
  voices (live single-pass clustering vs authoritative offline diarization).

## Conventions

- **Degrade to null.** Every optional subsystem (coach, speaker-id, memory store, Ollama) is built via a
  `TryCreate...` that returns null when its models/services are absent; callers no-op rather than fail,
  and end-of-meeting work is wrapped in best-effort `try/catch`. Preserve this when adding subsystems.
- **No em-dashes** anywhere (code, comments, strings, commit messages, prose). Use commas, parentheses,
  or full stops.
- **PowerShell is the primary shell.** Generate `.ps1`, not bash. The Bash tool is Git Bash: no heredocs,
  no PowerShell syntax inside it.
- Write prose in the first person ("I"), not "we".

## Tooling layout (`tools/`)

Empirical harnesses, kept because designs here are validated by measurement, not reasoning alone:
- `LiveReplay` feeds recorded WAVs back through the real live pipeline to A/B model/clustering changes.
- `TranscriptReconcile` scores a transcript against a reference (WER, speaker attribution, timing).

The reconciliation/scoring code (`WordError`, `WordStream`, `Aligner`, `Metrics`, `VttParser`,
`CallScribeMd`, ...) lives in `tests/CallScribe.TestSupport` (shared by the test suite and the CLI), not
in the shipped assembly.

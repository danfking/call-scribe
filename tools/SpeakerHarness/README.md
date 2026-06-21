# SpeakerHarness

Offline, repeatable verification of the speaker-identification path — no live call needed.

It synthesises distinct far-side voices with Windows TTS (SAPI), then runs them through the
real sherpa-onnx embedder, diarizer, and `SpeakerResolver`, and checks that:

1. **Embedding discrimination** — two clips of the same voice are closer (cosine distance)
   than any cross-voice pair.
2. **Session clustering** — `SpeakerResolver` gives each voice a consistent, distinct label.
3. **Offline diarization** — a concatenated A|B|A recording splits into exactly two speakers,
   with A's two turns sharing a cluster and B's differing.

The mic ("Me") track is not exercised: capture is known to work, and only the Others track is
ever diarized.

## Run

```
scripts/coach-pull-speaker-models.ps1   # one-time: download the ONNX models
dotnet run --project tools/SpeakerHarness
```

Needs the speaker models present and at least two installed SAPI voices (Windows ships
Microsoft David + Zira). Exit code 0 = all checks passed.

Not covered here: the pgvector `VoiceprintStore` (enroll/identify persistence), which needs
Postgres up. Its matching is the same cosine search proven above; the live cross-meeting
path is validated by running `listen --speakers` on a real call.

# Closing the live-vs-Teams gap: can the FINAL batch pass be dropped?

Reference meeting: `2026-06-23-0931`, 8-person standup, Teams VTT as the gold reference.
Harness: `dotnet run --project tools/TranscriptReconcile -- --meeting 2026-06-23-0931 --teams <call.vtt>`

Measured scorecard (word-level, segmentation-independent):

| Reconciliation | WER | CER | word recall | precision | speakers | word attribution | timing (median / p90) |
|---|---|---|---|---|---|---|---|
| Live vs Teams  | 24.1% | 16.6% | 92.0% | 94.5% | 25 → 8 | 97% | 1.2s / 3.6s (offset −13.9s) |
| Final vs Teams | 19.5% | 14.7% | 93.6% | 94.2% | 8 → 8 | 91% | — |
| Live vs Final  | 26.0% | 18.1% | 92.6% | 94.6% | 25 → 8 | 94% | — |

Below, only claims that survived adversarial verification against the code are carried; corrections
from verification are called out.

## 1. Verdict

**Yes, live can replace final for this use case, but only after two changes ship: a larger live
model and live speaker consolidation.** Everything else is polish or measurement hygiene.

Live trails final by only ~5 WER points (24.1% vs 19.5%) with near-equal recall (92.0% vs 93.6%),
and live's word-attribution (97%) is actually *higher* than final's (91%). Live timing is already
good. So the live transcript's *content* is close to final quality. The live *speaker layer* is not:
25 labels where final makes 8 (Deon the facilitator splits ~10 ways), mostly generic "Speaker N".
That fragmentation, not attribution accuracy, is the real blocker, the one place final's
whole-recording clustering has authority that live's per-chunk online clustering can't match today.

Verification corrected three things:
- **The preamble is lost from the WAV too, not just live.** WASAPI capture only starts in `Start()`,
  which runs after model load (`ListenCommand.cs:147`, `CaptureEngine.cs:52`). Fixing capture-start
  ordering helps both paths (Item 7), more valuable than first thought.
- **The −13.9s offset is not a quality problem.** `Metrics.cs` subtracts the estimated global offset
  before computing abs-error, so it's absorbed. Rebasing the timeline (Item 8) is presentation-only.
- **The echo filter does not drop short "Me" back-channels** (`MinTokensForMatch=3` protects them);
  any such miss is a capture-start/segmentation issue, not echo-filter tuning.

Condition for dropping final: ship Items 1 and 2, re-run the harness on `0931` and `0933.others.wav`;
if live label count lands within ~1-2 of the true speaker count with attribution ≥ ~97% and WER
closes to ~21-22%, final can be retired. The bar is **parity with final, not parity with Teams**
(Teams VTT has its own errors; final-vs-Teams attribution is itself only 91%).

## 2. Prioritized roadmap (highest ROI first)

1. **Live model `base.en` → `small.en`** (config, low). The `--live-model` flag already accepts it
   (`ListenCommand.cs:21-25`); flip the default (config-backed so weak machines can opt down).
   Largest content lever: expect WER ~21-22%, CER ~14%, and fewer hallucination/repetition loops.
2. **Live speaker consolidation** (structural, high). Give `SpeakerResolver.AssignSession` a deferred
   pass mirroring offline `MergeSmallClusters` (`OfflineDiarization.cs:122-165`): fold low-count/low-
   second session centroids into the nearest substantial one, and retro-relabel the persisted live
   transcript. This is the blocker; targets the fragment tail without touching the 97% attribution.
3. **Loosen live merge threshold** (config, low; do before/with Item 2). Raise `SessionMergeDistance`
   (0.55 → ~0.7) and/or `LiveMinSpeakerSeconds`; tune empirically against `0931` + `0933.others.wav`
   (0.75 is the sherpa segmentation value, *not* a like-for-like for `AssignSession`).
4. **Enroll the recurring attendees once** (operational, low; after 2/3). Turns "Speaker N" into real
   names via stage-1 voiceprint matching. Does not reduce fragmentation by itself.
5. **Live Whisper initial prompt seeded with meeting vocabulary** (config, low). The live processor
   sets only `WithLanguage("en")` (`LiveCaptionEngine.cs:125`); seed SM/SM2, Angular, ADR, names.
6. **Repetition-collapse guard on live captions** (structural, low). Extend `IsNonSpeechAnnotation`
   (`LiveCaptionEngine.cs:285`) to drop a single token repeated N+ times ("as soon as soon as…").
7. **Fix capture-start ordering** (structural, medium). Start capture immediately / pre-warm the
   model so the opening ~14s isn't lost from both live and the saved WAV.
8. *(optional)* **Rebase the live timeline** to the recording epoch (presentation only; collapses the
   misleading −13.9s offset, improves no accuracy metric).

## 3. Re-measure per item

Re-run `TranscriptReconcile` after each change and diff the report; track the four headline metrics
(Live-vs-Teams WER, recall, label count, attribution). Speaker work also runs against
`0933.others.wav`. Pass signals: Item 1 → WER ~21-22%, substitutions drop from 181; Items 2-3 →
label count near 8 with attribution ≥97%; Item 4 → most labels resolve to names; Item 7 → opening
preamble lines present in live.

## 4. Drop / deprioritize

- Treating the −13.9s offset as a quality problem (it's absorbed by the metric).
- Tightening `MaxWindow`/`MinWindow` for timing (median 1.2s already meets the goal; shorter windows
  *worsen* fragmentation, fighting Item 2).
- VAD-aware live segmentation as a near-term item (high effort for a few WER points Item 1 delivers
  cheaper; revisit only if WER still blocks the drop after Item 1).
- Echo-filter retuning for dropped back-channels (mechanism refuted).
- A low-SNR "skip resolution" guard (redundant with Item 3).

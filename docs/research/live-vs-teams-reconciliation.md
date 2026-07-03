# Closing the live-vs-Teams gap: can the FINAL batch pass be dropped?

Reference meeting: `2026-06-23-0931`, 8-person standup, Teams VTT as the gold reference.
Harness: `dotnet run --project tools/TranscriptReconcile -- --meeting 2026-06-23-0931 --teams <call.vtt>`

Measured scorecard (word-level, segmentation-independent):

| Reconciliation | WER | CER | word recall | precision | speakers | word attribution | timing (median / p90) |
|---|---|---|---|---|---|---|---|
| Live vs Teams  | 24.1% | 16.6% | 92.0% | 94.5% | 25 → 8 | 97% | 1.2s / 3.6s (offset −13.9s) |
| Final vs Teams | 19.5% | 14.7% | 93.6% | 94.2% | 8 → 8 | 91% | n/a |
| Live vs Final  | 26.0% | 18.1% | 92.6% | 94.6% | 25 → 8 | 94% | n/a |

Below, only claims that survived adversarial verification against the code are carried; corrections
from verification are called out.

## 1. Verdict

**Yes, live can replace final for this use case, but only after two changes ship: a larger live
model and live speaker consolidation.** Everything else is polish or measurement hygiene.

> **Update (2026-06-23, both blockers shipped).** Item 1 (`small.en` default, #34) and Item 2 (live
> speaker consolidation, #35) are in. Re-measured on `0931`: content is unchanged in shape (live
> ~22-24% WER vs final 19.5%, recall 92% vs 94%, live attribution 98% ≥ final 93%); the speaker
> blocker is resolved, consolidation folds **24 far-side labels → 8** at **96% attribution held**,
> matching final's 8. So live now clears the bar the verdict set. The remaining gap is **not** a
> metrics one, it is a product gap: the saved `.md` artifact is still produced only by the final
> pass, and the live transcript lives in the coach DB (needs `--coach`). To actually drop/skip final
> in practice, the consolidated live transcript must be exportable to the saved `.md` (and offline
> diarization's speaker-enrollment side effect would be lost). Recommendation: make final **optional**
> (a fast `--live-only` path that exports the live transcript), not removed, so users who want the
> extra ~3-5 WER points and authoritative diarization can still opt in.

> **Update (2026-07-02, default flipped, #61).** The optional path shipped first as `--live-only`,
> then became the default: plain `start` (the merged record+captions+transcribe command, formerly
> `listen`) now saves the consolidated live transcript, and the slow
> batch pass (plus offline diarization/naming/enrollment) is opt-in via `--full`. The predicted
> lost-side-effect is real, so coaching profiles are now refined from the live transcript on the
> default path and from the offline-attributed transcript under `--full`. The batch pass is not
> removed; the DB dependency stands, with a documented fallback to `--full` when the DB is down.

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
2. **Live speaker consolidation** (structural, high). **Done (#35).** A deferred `SpeakerResolver.Consolidate`
   pass mirroring offline `MergeSmallClusters` (`OfflineDiarization.cs:122-165`), wired into the
   `start` stop flow, which rewrites the persisted live transcript via `IMemoryStore.RelabelAsync`.
   Measured A/B on `0931` (LiveReplay + reconcile vs Teams): far-side labels **24 → 8** (true ~7) with
   word attribution **flat at 96%** and WER unchanged. Key finding: a blanket agglomerative "merge the
   closest pair" collapses *distinct* speakers (Kiel+Deon sit <0.72 apart on noisy live embeddings, so
   attribution fell to ~55-65%). The working form folds only **low-support fragments** (`< minClips`,
   default 3) into the nearest **substantial** cluster within `SpeakerConsolidationDistance` (0.80) and
   never merges two substantial clusters (that protection is what holds attribution).
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

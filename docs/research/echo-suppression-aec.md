# Speaker bleed in dual-track call recording: research report

> Background research for issue #6 (echo suppression residual). Sources are
> third-party and self-reported figures are order-of-magnitude; see the
> confidence notes at the end. This document informs the design decision, it is
> not itself a spec.

**Context:** call-scribe records two WASAPI streams (loopback = "others", mic =
"me") and transcribes with Whisper.net. On open speakers, far-side audio bleeds
into the mic, gets double-captured, and naive transcription labels the far-side
speech as the local speaker. Today the app handles this purely in text via
`CrossTrackEchoFilter` (token-overlap matching after transcription). This report
covers how to fix it properly.

---

## 1. AEC as the standard solution

The industry-standard fix is **Acoustic Echo Cancellation (AEC) applied to the
mic before transcription, using the loopback/render audio as a time-aligned
reference**. AEC consumes two synchronized streams: the render/far-end signal
(the loopback) and the near-end mic capture. It models the speaker-to-mic
acoustic path with an adaptive filter, then subtracts the echo estimate from the
mic so the bled-in far-side speech never reaches the recognizer. The reference
signal is what distinguishes AEC from plain noise suppression.

| Engine | What it is | Maturity / accuracy | License |
|---|---|---|---|
| **WebRTC AEC3 / APM** | De-facto open standard, shipped in Chrome. Partitioned-block frequency-domain adaptive filter. API: `ProcessReverseStream()` (render) then `ProcessStream()` (mic). | Most battle-tested open AEC; ~20-40 dB echo removal. Bundles AEC + noise suppression + AGC + high-pass. | BSD 3-Clause |
| **SpeexDSP** | Classic MDF adaptive filter. `speex_echo_cancellation(input, echo_ref, output)`. | Older, lighter, linear-only (cannot model non-linear distortion). Simple to embed; weaker than AEC3 on hard echo paths. | BSD 3-Clause |
| **Krisp** | Proprietary AI engine doing AEC + noise + room-echo removal as a virtual mic/speaker layer. | Strong, turnkey; commercial SDK. Local processing. | Proprietary/commercial |
| **RNNoise** | Noise suppression only. No reference signal. | Red herring for this problem: without a reference it structurally cannot cancel far-side bleed. | BSD |

**Key constraint:** the reference must *lead* the echo and stay time-aligned.
WebRTC AEC3 continuously cross-correlates reference against capture to track a
speaker-to-mic delay that runs ~20-200 ms and drifts during a call. A few ms of
misalignment and the linear filter fails to converge, leaving residual echo
(see section 7).

Sources: https://switchboard.audio/hub/how-webrtc-aec3-works/ ,
https://webrtc.googlesource.com/src/+/main/LICENSE ,
https://docs.livekit.io/reference/python/v1/livekit/rtc/apm.html ,
https://www.speex.org/docs/manual/speex-manual/node7.html ,
https://github.com/xiph/speexdsp , https://krisp.ai/developers/ ,
https://jmvalin.ca/demo/rnnoise/

---

## 2. Windows Voice Capture DSP (CWMAudioAEC DMO)

Windows ships its own AEC engine, callable from any process. The most pragmatic
native option.

- **What it is:** a DirectX Media Object created via
  `CoCreateInstance(CLSID_CWMAudioAEC)`, implemented in `Mfwmaaec.dll`, header
  `Wmcodecdsp.h`, available since Vista. Bundles AEC, mic-array processing,
  noise suppression, AGC, and VAD, each toggleable. Exposes `IMediaObject` and
  `IPropertyStore`; it does **not** implement `IMFTransform`.
- **Source mode vs filter mode:**
  - *Source mode* (recommended, easier): the DMO opens and synchronizes the
    capture and render devices itself. You just read the cleaned output. It
    handles stream alignment internally, which is the big win given
    call-scribe's current architecture.
  - *Filter mode*: you feed mic samples on input stream 0 and speaker/reference
    samples on input stream 1 via `ProcessInput`, then pull cleaned audio with
    `ProcessOutput`. You own the alignment.
- **Inputs/outputs and constraints:** AEC is single-channel; the
  speaker/reference line must be mono; supported sample rates are 8000 / 11025 /
  16000 / 22050 Hz; 16-bit PCM or IEEE float. These match Whisper.net's
  preferred 16 kHz mono exactly, so no quality is lost feeding its output to
  Whisper.
- **Using it from .NET:** COM interop. There is no purpose-built NuGet wrapper
  for this DSP. NAudio's `NAudio.Dmo` assembly (`MediaObject`,
  `DmoOutputDataBuffer`, etc.) provides helper types to build on. Microsoft's
  official sample is C++ (`microsoft/Windows-classic-samples`); port the COM
  calls to C#.
- **Limitations:** mono reference only; fixed low sample-rate set; classical DSP
  canceller (good, not best-in-class versus AEC3 on hard non-linear echo paths);
  requires hand-written COM interop.

Sources: https://learn.microsoft.com/en-us/windows/win32/medfound/voicecapturedmo ,
https://github.com/naudio/NAudio/blob/master/NAudioTests/Dmo/ResamplerDmoStreamTests.cs

---

## 3. .NET / NAudio accessibility, ranked by practicality

NAudio gives capture (`WasapiCapture`) and the loopback reference
(`WasapiLoopbackCapture`), but does no AEC itself and does not expose Windows'
AEC controls.

1. **Windows Voice Capture DSP (DMO), source mode — most practical.** Free,
   native, Win10/11, self-aligning, 16 kHz mono out. Cost: hand-written COM
   interop. Best risk/effort tradeoff.
2. **SpeexDSP via `SpeexDSPSharp.Core` — lightest managed dependency.** NuGet,
   MIT wrapper, .NET 8 / netstandard2.0, prebuilt Windows native binary. Call
   frame-by-frame (mic + reference in, cleaned mic out). You own alignment.
   Weaker than AEC3.
3. **WebRTC APM via `SoundFlow.Extensions.WebRtc.Apm` — best quality, heaviest
   integration.** MIT wrapper over BSD native. AEC delivered as a
   `WebRtcApmModifier` node inside the SoundFlow audio graph, not a standalone
   call; small/new package. You'd adopt SoundFlow or P/Invoke the native APM.
4. **Windows 11 WASAPI communications AEC (`IAcousticEchoCancellationControl`) —
   least reliable.** Win11 22621+; only *controls* a system AEC that exists only
   if the driver/OEM ships an AEC APO, so availability and quality are not
   guaranteed. NAudio doesn't expose it (issue #1223).

Sources: https://www.nuget.org/packages/SpeexDSPSharp.Core/ ,
https://www.nuget.org/packages/SoundFlow.Extensions.WebRtc.Apm ,
https://github.com/LSXPrime/webrtc-audio-processing ,
https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iacousticechocancellationcontrol ,
https://github.com/naudio/NAudio/issues/1223

---

## 4. How the real products handle speaker bleed

**Fix the signal (own AEC, on by default):**
- **Zoom** — software AEC on by default; levels are Auto / Low / High. "Original
  Sound" / High-Fidelity Music Mode disables AEC (the headphones exception).
- **Microsoft Teams** — ML-based AEC plus de-reverberation; auto-mutes a second
  nearby device on the same meeting. (The exact "echo detected" banner string is
  unverified; the documented mechanism is nearby-device auto-mute.)
- **Krisp** — purpose-built AEC as a virtual mic/speaker layer; marketed as the
  alternative to headphones on speakerphone.

**Fix the labels (diarization, rely on upstream AEC):**
- **Otter.ai** — no in-house AEC; consumes the platform's cleaned stream or raw
  mic, leans on diarization. Even warns users to disable Chrome AEC (over-cleans).
- **Fireflies.ai** — no documented own AEC; bot gets the platform's processed
  stream; diarization for labels.

**Fix the capture (isolated tracks):**
- **Descript** — per-participant tracks at source; its echo/reverb removal
  targets room reverb, not far-side acoustic bleed.

Takeaway: serious real-time tools fix the signal with their own AEC. The
note-takers that don't get away with it only because the meeting app already ran
AEC. call-scribe records raw OS loopback + raw mic with no meeting-app AEC in the
path, which is exactly why bleed reaches it raw.

Sources: https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0066398 ,
https://www.microsoft.com/en-us/microsoft-365/blog/2022/06/13/how-microsoft-teams-uses-ai-and-machine-learning-to-improve-calls-and-meetings/ ,
https://krisp.ai/blog/acoustic-echo-cancellation/ ,
https://help.otter.ai/hc/en-us/articles/11892740449815-Otter-transcript-missing-audio-when-using-a-Chrome-browser ,
https://guide.fireflies.ai/articles/6455008939-improve-the-quality-of-speech-recognition ,
https://www.descript.com/blog/article/multitrack-recording-edit-mix-and-add-effects-to-your-podcast

---

## 5. Audio-level AEC vs post-transcription text dedup

**Approach A — audio AEC before ASR.**
- Pros: the only method that improves recognition *accuracy* on the corrupted
  track, because bled words never reach Whisper.
- Cons / failure modes: needs time-aligned reference + mic; convergence/delay
  tracking; artifact risk if the residual suppressor is too aggressive
  ("hollow/underwater" speech).

**Approach B — post-transcription text dedup (`CrossTrackEchoFilter` today).**
- Pros: simple, no DSP, no alignment.
- Cons / failure modes: the bled copy is quieter and distorted, so Whisper
  transcribes it *differently* on each track, making fuzzy matching brittle
  (worst under loud bleed). Hard ceiling: it can relabel/delete duplicates but
  cannot recover recognition errors already baked in by the overlap.

**Double-talk breaks both:** AEC must freeze adaptation during overlap (residual
leaks); text dedup has no clean duplicate to find, and ASR error rates on
overlapping speech can exceed 50%.

**"Cleaning hurts ASR" caveat, scoped:** blind *noise* suppression / generic
enhancement often raises WER, but that targets spectral-guesswork denoisers, not
reference-driven AEC. Echo bleed is a correlated copy of a signal you hold (the
reference), so linear AEC subtracts it without guesswork. Use AEC, keep the
residual suppressor gentle, avoid blind denoising.

**Keep `CrossTrackEchoFilter`?** Yes, as a cheap backstop for residual leakage
after AEC. AEC-as-primary with light text dedup as a safety net; nobody
recommends text dedup alone for this problem.

Sources: https://sonix.ai/resources/fix-mic-bleed-multi-track-recordings/ ,
https://apxml.com/courses/introduction-to-speech-recognition/chapter-5-decoding-and-putting-it-all-together/common-challenges-in-speech-recognition ,
https://www.assemblyai.com/blog/noise-cancellation-stt-pros-cons

---

## 6. Optional target-speaker VAD / diarization complement

Diarization and voiceprints solve a *labelling* problem, not a *signal-quality*
one. They can relabel/filter bled segments but cannot restore intelligibility on
echo-corrupted audio.

- Dual-track already does most of diarization's job for free (one speaker per
  channel). Diarization only helps when a channel carries multiple voices, which
  is exactly the bleed case, and single-channel separation is where diarization
  is weakest.
- The genuinely useful complement is **target-speaker VAD / Personal VAD**:
  enroll the local user's voiceprint (ECAPA-TDNN / x-vector), keep only mic
  frames matching that profile, drop the rest.
- Runs in .NET via `sherpa-onnx` (official C#/.NET API, local). Caveat: a
  tracked issue reports its port doesn't exactly match reference pyannote
  accuracy.
- Worth it only after AEC is in place and only if residual bleed/mislabeling
  persists. Phase-3 nicety, not a fix.

Sources: https://developers.deepgram.com/docs/multichannel-vs-diarization ,
https://arxiv.org/pdf/2204.03793 , https://github.com/k2-fsa/sherpa-onnx ,
https://github.com/k2-fsa/sherpa-onnx/issues/1708

---

## 7. The capture-architecture constraint (the crux)

**Why AEC needs a continuous, time-aligned reference:** the adaptive filter
aligns the reference (what was played) against the capture (what the mic heard).
The reference must lead the echo, the delay must be tracked, and the reference
timeline must be continuous. A few ms of misalignment and the filter won't
converge.

**Why call-scribe's current capture breaks that:** `CaptureEngine` runs two
independent WASAPI clients (`WasapiLoopbackCapture`, `WasapiCapture`), each with
its own free-running clock, aligned only loosely by padding silence against a
shared `Stopwatch` epoch with ~250 ms tolerance (`CaptureTrack`). Fine for a
wall-clock merge, not sample-accurate, and the clocks can drift. Worse,
`WasapiLoopbackCapture` emits no `DataAvailable` while nothing plays, so the
reference timeline goes silent during gaps. Both are fatal to frame-by-frame AEC.

**Two remedies:**
1. **Let the DMO handle alignment (source mode).** The Voice Capture DSP in
   source mode opens and synchronizes the devices itself, sidestepping the
   two-clocks problem. Lowest-risk path.
2. **Rework capture to feed a gap-filled, sample-aligned loopback reference.**
   For filter-mode AEC (DMO, Speex, or WebRTC APM): synthesize silence into the
   live reference feed during loopback gaps, and establish a known, stable
   sample offset between mic and loopback (resample to common 16 kHz mono, track
   the speaker-to-mic delay).

Relevant files: `src/CallScribe/Audio/CaptureEngine.cs`,
`src/CallScribe/Audio/CaptureTrack.cs`,
`src/CallScribe/Transcription/CrossTrackEchoFilter.cs`.

Sources: https://switchboard.audio/hub/how-webrtc-aec3-works/ ,
https://github.com/naudio/NAudio/blob/release/2.x/Docs/WasapiLoopbackCapture.md

---

## 8. Recommended path for call-scribe, in phases

**Phase 1 — Adopt native AEC, source mode (root-cause fix, lowest risk).**
Insert the Windows Voice Capture DSP (CWMAudioAEC DMO) in source mode between mic
capture and Whisper. Free, native to Win10/11, self-aligning, 16 kHz mono out.
Avoids the two-clock alignment problem. Effort: COM interop (port Microsoft's
C++ sample), building on `NAudio.Dmo`.

**Phase 2 — Keep `CrossTrackEchoFilter` as a backstop.** Catch residual leakage
that survives AEC. Belt-and-braces; text dedup alone is what we're moving away
from, not what we remove.

**Phase 3 — If quality is insufficient, escalate the canceller.** Move to WebRTC
APM (SoundFlow or own P/Invoke) for stronger cancellation, accepting heavier
integration; `SpeexDSPSharp.Core` is the lighter middle option. A filter-mode
canceller requires the capture rework from section 7 remedy 2.

**Phase 4 (optional) — Target-speaker VAD complement.** Only if
mislabeling/bleed persists after AEC.

**Do not:** use RNNoise (no reference, can't cancel echo) or expect plain
diarization to fix this (it relabels, it doesn't clean the signal).

---

## Confidence notes

- DER/WER figures are vendor/paper self-reported and dataset-specific;
  order-of-magnitude only.
- Corrections from source-checking: Zoom AEC levels are Auto/Low/High; the Teams
  "echo detected" banner string is unverified (documented mechanism is
  nearby-device auto-mute).
- Some help-center quotes are snippet-sourced (sites 403 automated fetching).
- Strongest claim: labelling techniques correct *attribution*; only signal-level
  AEC restores transcription *accuracy* on echo-corrupted audio, and AEC
  requires a continuous time-aligned reference + mic, which dictates how audio
  must be captured.

# EchoHarness

A measurement tool for issue #6 (speaker bleed). It plays a far-side speech clip
through the speakers across a sweep of device-volume levels, runs call-scribe's
live capture and caption pipeline with the mic silent, and counts how many
far-side words leak into the "Me" side. During far-side-only playback in a quiet
room, any Me caption is bleed.

This establishes a baseline before the Phase 1 acoustic echo cancellation work
(#7), and gives a before/after once AEC lands.

## Requirements

- Run on **speakers, not headphones**. Acoustic bleed is the effect under test.
- A quiet room. Do not speak during a run.
- A far-side clip (mp3 or wav). Generate one with `scripts/generate-echo-clips.ps1`
  (needs `pip install edge-tts`).

## Run

```powershell
./scripts/generate-echo-clips.ps1          # once, makes artifacts/echo-clips/farside.mp3
./scripts/run-echo-baseline.ps1            # builds + runs the sweep
```

Or directly:

```powershell
dotnet run --project tools/EchoHarness -c Release -- `
    --clip artifacts/echo-clips/farside.mp3 `
    --volumes 0.1,0.25,0.5,0.75,1.0 `
    --tail 12 --live-model base.en
```

## Output

A per-volume table of `others` (far-side captions detected, a sanity check that
audio actually played and was captured) and `me-bleed` (far-side words that
leaked into the Me track). It also lists the leaked Me captions so you can
confirm they are genuinely the far side, and writes `echo-baseline.csv` into the
recordings directory.

The harness sweeps and then restores the render device's master volume.

## How it observes captions

It subscribes to `LiveCaptionEngine.CaptionEmitted`, which fires for Others
captions as soon as they transcribe and for Me captions only after they survive
echo suppression. Suppressed bleed never fires the event, so a reported Me
caption is genuinely leaked output, not something the filter caught.

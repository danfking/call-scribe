# Building a polished dashboard TUI for call-scribe: Spectre.Console vs Terminal.Gui (and friends)

Research only. No code was changed in the repo. Date: 2026-06-19.

## The question

call-scribe is a live dual-track call-transcription tool. During a call it shows two tracks, "Me" and "Others", each with a live state (listening / hearing audio / transcribing), plus a scrolling caption stream. It runs in Windows Terminal, targets `net10.0-windows`, and already depends on Spectre.Console (currently 0.51.1; see `src/CallScribe/CallScribe.csproj`).

The owner wants two things:

1. A dashboard look, with **icons** for the track states now.
2. A foundation that can grow a **menu system** for more features later.

A prior attempt rendered geometric Unicode symbols as "?" (an output-encoding bug). The current code works around this by staying ASCII-only in `LiveStatusDisplay.cs` and notes the cause in a comment.

This report compares the realistic options and ends with a recommendation.

---

## TL;DR recommendation

**Stay on Spectre.Console for the dashboard now, and reach for Terminal.Gui v2 only if and when you actually build a real menu-driven app shell.** They do not coexist in one screen, so treat them as two doors, not two layers.

Concretely, in priority order:

1. **Fix the encoding first, regardless of framework.** Set `Console.OutputEncoding = System.Text.Encoding.UTF8` at the very top of `Main`, before any rendering. This is the actual cause of the "?" symbols and it is independent of the UI choice. (See section 4.)
2. **Build the dashboard with Spectre.Console `Live` + `Layout`.** It does multi-panel live rendering well, it is already a dependency, and the migration cost is near zero. Use **geometric/box-drawing Unicode** (● ▶ ■ ◆) for state icons as the default look, because those glyphs exist in stock Cascadia and only need UTF-8 to render.
3. **Add menus as Spectre `SelectionPrompt` screens** for now. They are one-shot and take over the screen, which is fine for a "press M for menu, pick an action, return to the dashboard" flow.
4. **Keep the existing plain fallback** for redirected output. Spectre already degrades, and `LiveStatusDisplay` already disables itself when `Console.IsOutputRedirected`.
5. **Only adopt Terminal.Gui v2 if you want a true app shell**: a persistent menu bar and status bar that stay on screen while the dashboard updates, mouse support, and keyboard navigation between focusable panels. That is a real rewrite of the UI layer, not an add-on. The good news for this app: Terminal.Gui v2 stable now targets `net10.0`, which matches call-scribe exactly.

The key tradeoff: Spectre is a **renderer plus blocking prompts**; Terminal.Gui is a **windowing/event-loop framework**. Spectre cannot give you a persistent menu bar alongside live content. Terminal.Gui can, at the cost of rewriting the UI and learning a UI-thread marshaling model.

---

## 1. Spectre.Console for live dashboards

### What it does well

Spectre.Console (current stable 0.57.0, published 2026-06-11; targets .NET Standard 2.0 through .NET 10) is a strong **renderer** for a multi-panel live dashboard. ([NuGet](https://www.nuget.org/packages/Spectre.Console/))

- **`AnsiConsole.Live()`** "enables updating console output without scrolling." You pass an initial renderable and get a context. To repaint in place you call `ctx.Refresh()`; to swap the whole display you call `ctx.UpdateTarget()`; `.StartAsync()` handles async work. ([Live rendering how-to](https://spectreconsole.net/console/how-to/live-rendering-and-dynamic-updates))
- **`Layout`** "divides console space into sections that can be split horizontally or vertically." `SplitColumns` / `SplitRows`, named regions via `layout["Section"]`, `.Update(...)` to replace a region's content, sizing with `Size()` / `Ratio()` / `MinimumSize()`, plus nesting and an `IsVisible` toggle. This is the backbone of a dashboard: build the Layout once, mutate named sections, call `ctx.Refresh()`. ([Layout widget](https://spectreconsole.net/console/widgets/layout))
- **Panel** (bordered, padded box), **Rule** (a divider that doubles as a section header with a title), **Table** (live updates via `UpdateCell()` / `InsertRow()`), **Columns / Rows / Grid** (arrangement helpers; Grid is borderless tabular), **FigletText** (banner header text), **Canvas** (`SetPixel(x, y, color)` for sparklines/graphics). ([Rule](https://spectreconsole.net/console/widgets/rule), [Table](https://spectreconsole.net/console/widgets/table), [Figlet](https://spectreconsole.net/console/widgets/figlet), [Canvas](https://spectreconsole.net/console/widgets/canvas))
- **Status** (`AnsiConsole.Status()`, animated spinner for indeterminate work) and **Progress** (determinate task bars). Progress auto-refreshes (`AutoRefresh` defaults true, `RefreshRate` defaults to 10/second). ([Status](https://spectreconsole.net/console/live/status), [Progress](https://spectreconsole.net/console/live/progress))

For the Me/Others dashboard, the natural shape is a `Layout` split into a header (Figlet or Rule), two track panels side by side, and a caption region below, all driven by one `ctx.Refresh()` loop.

### Refresh and flicker

`Live` is **manually driven**: you decide when it repaints by calling `ctx.Refresh()`. There is no documented numeric auto-refresh rate for `Live` specifically (the 10/second default belongs to Progress). ([Live display](https://spectreconsole.net/console/live/live-display))

A flicker regression existed in **0.48.0**: the repaint changed from a single cursor-up to an erase-line-plus-cursor-up repeated per line, which flickered on Windows. It was fixed in PR #1504. Current versions should not flicker, but the lesson holds: keep the live target's height stable and the refresh rate modest. ([Flicker regression issue #1466](https://github.com/spectreconsole/spectre.console/issues/1466))

### The decisive limitation: no persistent menu, no event loop, no mouse

This is what settles the architecture question. **Spectre.Console is render-and-prompt, not an event-loop TUI.**

- **Live rendering is single-threaded and exclusive.** Direct quote: *"Live rendering is not thread safe. Using it together with other interactive components such as prompts, progress displays, or status displays is not supported."* So you cannot run a prompt or a second live component while the dashboard refreshes. ([Live rendering how-to](https://spectreconsole.net/console/how-to/live-rendering-and-dynamic-updates))
- **Menus are one-shot, blocking, full-screen prompts.** `SelectionPrompt` lets users "navigate with arrow keys to select one option," and `AnsiConsole.Prompt()` "blocks execution until the user makes a selection," returning a single value. *"Selection prompts are not thread safe. Using them together with other interactive components ... is not supported."* ([Selection prompt](https://spectreconsole.net/console/prompts/selection-prompt))
- **No mouse support.** Nothing in the API or docs exposes mouse events.
- **No non-blocking key/event loop.** Input is built on `Console.ReadKey`, described in the project's own discussion as "a blocking call and not well-suited for multi-threaded applications." A feature request for a non-blocking key listener (#1605) sits untriaged, which confirms the feature does not exist today. ([Issue #1605](https://github.com/spectreconsole/spectre.console/issues/1605))

**Conclusion:** Spectre renders a beautiful live dashboard, and it gives you arrow-key menus as separate blocking screens. It cannot host a persistent menu bar or status bar that stays on screen while the dashboard updates. To get that you would have to write your own non-blocking input loop (`Console.KeyAvailable` / `ReadKey` polling) and reconcile it by hand with the single Live render thread. At that point you are rebuilding the part of a TUI framework that Terminal.Gui already gives you.

---

## 2. Terminal.Gui v2

### Status, version, and the .NET target that matters here

Terminal.Gui (originally Miguel de Icaza's `gui.cs`, now maintained by the gui-cs org) is a full TUI framework: windows, menu bar, status bar, dialogs, 50+ view controls, computed layout, and first-class keyboard and mouse input.

- **v2 is released and stable.** Both the official site and the GitHub README carry the heading "Version 2.0 Has Been Released." Some web snippets still call v2 "prealpha"; they are stale. ([Official site](https://gui-cs.github.io/Terminal.Gui/), [repo](https://github.com/gui-cs/Terminal.Gui))
- **Current stable: 2.4.6, published 2026-06-12.** ~1.8M total downloads. ([NuGet](https://www.nuget.org/packages/Terminal.Gui))
- **Target framework: `net10.0`** (verified on NuGet; `net10.0-windows` is listed as compatible). v2 dropped .NET Standard and older runtimes that v1 multi-targeted (net472/netstandard2.0/2.1/net6/net8). **For call-scribe this is a lucky alignment: the app is already `net10.0-windows`, so the framework constraint that would block many projects is a non-issue here.** ([NuGet 2.4.6](https://www.nuget.org/packages/Terminal.Gui/2.4.6))

### Programming model

- Classic lifecycle is `Application.Init()` / `Application.Run()` / `Application.Top` around a single-threaded main loop. v2 adds an instance-based `IApplication` model (`Create()` / `Init()` / `Run()`), marks `Shutdown()` obsolete in favour of `Dispose()`, and runs its input thread at ~50 polls/second. ([What's new in v2](https://gui-cs.github.io/Terminal.Gui/docs/newinv2.html))
- Layout uses `Pos`/`Dim` (now public, first-class), with `Dim.Auto`, `Pos.Align`, and `AnchorEnd` for responsive layouts. The v1 Absolute-vs-Computed layout distinction was removed; adornments (`Margin`, `Border`, `Padding`) are on every View. ([What's new](https://gui-cs.github.io/Terminal.Gui/docs/newinv2.html))
- Keyboard and mouse were rewritten: a high-level `Key` class with a key-binding-to-`Command` system, and `MouseEventArgs` with granular events. Mouse and keyboard navigation are first-class. ([What's new](https://gui-cs.github.io/Terminal.Gui/docs/newinv2.html))

### Updating views in real time from a background thread

This is the make-or-break detail for a live transcription feed, and it is documented plainly.

- The thread-affinity rule (quoted): *"All UI operations must happen on the main thread. Attempting to modify views or their properties from background threads will result in undefined behavior and potential crashes."*
- The marshaling rule (quoted): *"Always use `App?.Invoke()` (from within a View) or `app.Invoke()` (with an IApplication instance) to update the UI from background threads."*
- Periodic updates: *"Use `App?.AddTimeout()` for periodic updates"* (and remove timers on dispose). async/await is recommended because awaited continuations return to the main thread. ([Multitasking deep-dive](https://gui-cs.github.io/Terminal.Gui/docs/multitasking.html))

In v1 the same mechanism was `Application.MainLoop.Invoke(...)`, which runs the action "the next time the main loop wakes up." ([Issue #145](https://github.com/gui-cs/Terminal.Gui/issues/145), [Issue #155](https://github.com/gui-cs/Terminal.Gui/issues/155))

Practical shape for call-scribe: keep audio/transcription on background threads, and every time new caption text arrives call `App.Invoke(() => captionView.Text = ...)` (or push periodic redraws via `AddTimeout`). Never touch a view off-thread. This is the classic WinForms-style footgun: forget `Invoke` and you crash.

### Maturity and migration cost

~11k GitHub stars, a long and deliberate v2 prerelease cycle before the stable release, now iterating at 2.4.x. v2 is **not** source-compatible with v1 (layout, input, and lifecycle all changed; there's an official migration guide). For call-scribe the relevant cost is not v1->v2 but Spectre->Terminal.Gui: rewriting `LiveStatusDisplay` and the listen-mode console flow into Views, learning the event loop and the `Invoke` discipline, and rethinking how captions stream into a scrolling view. That is a focused but real piece of work, not a swap. ([Migration guide](https://github.com/gui-cs/Terminal.Gui/blob/v2_develop/docfx/docs/migratingfromv1.md))

---

## 3. Other options, briefly

**Consolonia** (Avalonia UI rendered to the terminal). It brings real XAML + Avalonia MVVM/data-binding/theming to a text console, with mouse and keyboard. Latest release v11.3.12.6 (5 May 2026), ~800 stars, self-described **beta**. ([repo](https://github.com/Consolonia/Consolonia), [QuickStart](https://github.com/Consolonia/Consolonia/wiki/QuickStart-from-scratch)). Verdict: worth a spike only if you specifically want XAML/MVVM and an app-grade dashboard. For a CLI that needs a dashboard plus a menu, adopting the whole of Avalonia is overkill, and beta status argues against betting the shipped UI on it. Revisit if you ever outgrow both Spectre and Terminal.Gui.

**Raw VT / ANSI escape sequences** (no framework). Enable with `ENABLE_VIRTUAL_TERMINAL_PROCESSING` via `SetConsoleMode`; the full vocabulary (cursor positioning, 24-bit colour, alternate screen buffer, scroll margins) is in Microsoft's reference. Available since Windows 10 1511. ([Console Virtual Terminal Sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)). Verdict: fine for one small bespoke effect (a custom status line or spinner) inside the existing Spectre app; the wrong foundation for the whole UI, because a dashboard-plus-menu means hand-building layout, focus, input parsing, and resize handling, i.e. your own half-finished TUI toolkit.

---

## 4. Icons and the "?" encoding fix

### Why the symbols showed as "?"

Two Microsoft-documented facts combine:

1. The console's default output code page comes from the system locale and is usually a legacy OEM page (e.g. 437 US, 850 Western European) that maps only 256 characters. *"The default code page that the console uses is determined by the system locale."* ([Console.OutputEncoding](https://learn.microsoft.com/en-us/dotnet/api/system.console.outputencoding))
2. When you write a character that encoding cannot represent (a geometric shape or a Nerd Font glyph in OEM 437/850), .NET's encoder substitutes a literal question mark. *"The default value is a EncoderReplacementFallback object that replaces unknown input characters with the QUESTION MARK character ('?', U+003F)."* ([EncoderReplacementFallback](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoderreplacementfallback))

So the character is destroyed at the **encoding** stage, before the font is ever consulted. That is exactly the bug the prior attempt hit, and call-scribe currently never sets `Console.OutputEncoding` (verified: there is no `OutputEncoding` assignment anywhere in `src/`).

### The fix

Set this once, first thing in `Main`, before any rendering:

```csharp
Console.OutputEncoding = System.Text.Encoding.UTF8; // codepage 65001
```

Spectre.Console's own docs recommend the same guard, and Spectre keys its Unicode-vs-ASCII box-drawing decision off the output encoding, so setting UTF-8 also flips Spectre's Unicode capability on:

```csharp
if (Console.OutputEncoding.CodePage == 437)
    Console.OutputEncoding = System.Text.Encoding.UTF8;
```

([Understanding the rendering model](https://spectreconsole.net/console/explanation/understanding-rendering-model)). Microsoft's broader guidance is to "avoid code pages and use Unicode," setting 65001. ([Console code pages](https://learn.microsoft.com/en-us/windows/console/console-code-pages))

One caveat: **UTF-8 is necessary but not sufficient.** The glyph still has to exist in the terminal's font. *"successfully displaying Unicode characters to the console requires ... A font ... must define the particular glyph or glyphs to be displayed."* ([Console.OutputEncoding](https://learn.microsoft.com/en-us/dotnet/api/system.console.outputencoding)). UTF-8 makes geometric shapes work everywhere; it does **not** by itself make Nerd Font icons appear.

### Three icon strategies, ranked for this app

| Approach | Dashboard look | Reliability | Requires |
|---|---|---|---|
| **Geometric / box-drawing Unicode** (● ▶ ■ ◆ ┌─┐) | Clean, aligned, professional | **Highest** — present in essentially all monospace fonts incl. stock Cascadia | UTF-8 only |
| **Nerd Font glyphs** (true microphone/speaker/gear/record icons, in the Private Use Area) | **Best** — real icons | **Lowest** — needs a Nerd Font installed *and selected* in the terminal; Windows Terminal does not bundle one and you can't reliably detect one | UTF-8 + a Nerd Font in the terminal |
| **Emoji** | Colourful | Low/medium — documented width and monochrome-vs-colour bugs break column alignment | UTF-8 + emoji font |

Key facts behind the table:

- **Nerd Fonts** patch icon glyphs (microphone, speaker, gear, record dot, etc.) from Font Awesome / Material / Octicons into the Unicode Private Use Area; the icon renders only if the **terminal's configured font** is a Nerd Font. ([nerdfonts.com](https://www.nerdfonts.com/), [PUA code points](https://github.com/ryanoasis/nerd-fonts/wiki/Glyph-Sets-and-Code-Points))
- Microsoft ships **Cascadia Code / Mono** with Windows Terminal, and separately publishes **Cascadia Code NF** (Nerd Font) variants (since the 2404 release), but the request to *bundle* the NF variant with Windows Terminal was **closed as "not planned."** A user must install and select it manually. So you cannot assume a fresh Windows Terminal can show Nerd Font icons. ([Cascadia 2404 NF](https://devblogs.microsoft.com/commandline/cascadia-code-2404-23/), [terminal #18528 "not planned"](https://github.com/microsoft/terminal/issues/18528))
- **Emoji** width handling is inconsistent (variation selectors U+FE0E/FE0F not always honoured), and many symbols default to narrow monochrome "text" presentation rather than a colour icon. A maintainer quote: *"The default representation for ⚠ is 'narrow, single-width, non-emoji'."* Bad for a fixed-width dashboard. ([terminal #8970](https://github.com/microsoft/terminal/issues/8970), [discussion #13724](https://github.com/microsoft/terminal/discussions/13724))

### Graceful degradation

There is no reliable, portable way for a .NET console app to ask whether the terminal's font has a given glyph; the terminal renders the code point and you get "tofu" (an empty box) if it's missing. The established pattern (used by Starship, Powerlevel10k) is a **capability flag plus a parallel fallback table**, chosen by config rather than runtime detection:

- Default to safe geometric Unicode (works once UTF-8 is set).
- Offer an opt-in "Nerd Font mode" flag for users who have one selected.
- Offer a pure-ASCII fallback (`*`, `>`, `#`, `[REC]`) for redirected output, CI logs, or dumb terminals.

call-scribe already does the redirected-output half of this in `LiveStatusDisplay` (it disables the status line when `Console.IsOutputRedirected` or there's no real console). The recommended icon strategy slots cleanly into that existing flag.

**Best "dashboard" look vs reliability:** lead with geometric/box-drawing Unicode as the default (great look, works on stock Cascadia after the UTF-8 fix), make Nerd Font glyphs an opt-in enhancement, keep ASCII as the floor, and avoid emoji for anything that must stay column-aligned. ([encoding-and-glyph background](https://learn.microsoft.com/en-us/dotnet/api/system.console.outputencoding))

---

## 5. The decision for call-scribe

### Can Spectre and Terminal.Gui coexist?

Not on the same screen. Both assume exclusive ownership of the console: Spectre's `Live` is single-threaded and "not thread safe ... together with other interactive components," and Terminal.Gui runs its own main loop and alternate screen buffer. You can use them in **different modes** of one process (Spectre for non-interactive command output, Terminal.Gui for a full-screen `listen` shell), but you cannot layer a Spectre Live dashboard inside a Terminal.Gui window or vice versa. Treat the choice as one-or-the-other for any given screen.

### Option A: stay on Spectre (Live + Layout) and add prompt-based menus

- **Migration cost:** near zero. Spectre is already referenced; you bump the version, set `OutputEncoding`, and build a `Layout`-based dashboard. The existing redirect fallback stays.
- **What you get:** a polished live multi-panel dashboard with geometric-Unicode state icons, plus arrow-key menus as separate `SelectionPrompt` screens (press a key, the dashboard pauses, you pick an action, you return).
- **What you don't get:** a persistent menu bar or status bar visible *while* the dashboard refreshes, mouse support, or in-place keyboard navigation between panels. The menu is a mode switch, not an always-present chrome.

### Option B: adopt Terminal.Gui v2 for a real app shell

- **Migration cost:** a focused rewrite of the UI layer (`LiveStatusDisplay` and the listen-mode console) into Views, plus learning the event loop and the `App.Invoke` marshaling discipline for the caption feed.
- **What you get:** a genuine app shell, persistent `MenuBar` + `StatusBar` that stay on screen while content updates, mouse, keyboard navigation, dialogs, and a layout engine. This is the architecturally correct tool for "menu-driven dashboard."
- **The framework fit is good:** v2 stable targets `net10.0`, which is exactly call-scribe's TFM, so the usual adoption blocker (net8/net9 pinning) doesn't apply.

### Recommendation

**Do Option A now, keep Option B as the named upgrade path.** The owner wants the dashboard and icons *now*, and that is the part Spectre does cheaply and well; the menu system is a "later" requirement that prompt-based menus satisfy adequately for a first cut. The most important fix (UTF-8 output encoding) is shared by both options and unblocks the icons immediately.

Switch to Terminal.Gui v2 when the menu requirement hardens into "I want a real menu bar and status bar that live alongside the dashboard, with mouse and keyboard navigation." That is a deliberate rewrite, justified by a real app-shell need rather than done speculatively. Because the framework already matches the app's `net10.0` target, deferring the decision costs nothing.

Keep the plain redirected-output fallback in both cases: Spectre degrades on its own and `LiveStatusDisplay` already self-disables when output is redirected; a Terminal.Gui build would need an explicit "if redirected, fall back to line-by-line printing" branch, since a full-screen TUI has no meaning in a pipe.

---

## 6. Polished examples to crib from

**Spectre.Console**
- [spectreconsole/examples](https://github.com/spectreconsole/examples) — the official runnable gallery. The directly relevant samples are **Live**, **LiveTable**, **Layout**, **Status**, **Progress**, **Panels**, **Rules**, **Figlet**, and the composite **Showcase**. Run with `dotnet example live` etc. This is the closest thing to a template for the call-scribe dashboard.
- [spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console) and the [docs site](https://spectreconsole.net/) — the library and its polished screenshots.

**Terminal.Gui**
- [gui-cs/Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) **UICatalog** demo — the comprehensive interactive showcase of every control (menus, status bar, tables, trees, dialogs, themes, responsive layout). The best reference for full-screen interaction patterns.
- [Terminal.Gui Showcase](https://gui-cs.github.io/Terminal.Gui/docs/showcase.html) — real apps built on it: **Whale** (Docker manager), **TerminalGuiDesigner**, **Muse** (music player), **TermKeyVault**.

**Cross-language visual aspiration** (design reference only)
- [btop](https://github.com/aristocratos/btop) — the benchmark for a genuinely beautiful real-time monitoring dashboard (truecolor, braille graphs, box-drawing).
- [lazygit](https://github.com/jesseduffield/lazygit) — the gold standard for a persistent, keyboard-driven, multi-panel TUI layout.

---

## Sources

Spectre.Console: [Live rendering how-to](https://spectreconsole.net/console/how-to/live-rendering-and-dynamic-updates), [Live display](https://spectreconsole.net/console/live/live-display), [Layout](https://spectreconsole.net/console/widgets/layout), [Rule](https://spectreconsole.net/console/widgets/rule), [Table](https://spectreconsole.net/console/widgets/table), [Status](https://spectreconsole.net/console/live/status), [Progress](https://spectreconsole.net/console/live/progress), [Figlet](https://spectreconsole.net/console/widgets/figlet), [Canvas](https://spectreconsole.net/console/widgets/canvas), [Selection prompt](https://spectreconsole.net/console/prompts/selection-prompt), [Rendering model](https://spectreconsole.net/console/explanation/understanding-rendering-model), [Issue #1605 (no non-blocking key listener)](https://github.com/spectreconsole/spectre.console/issues/1605), [Issue #1466 (flicker)](https://github.com/spectreconsole/spectre.console/issues/1466), [NuGet](https://www.nuget.org/packages/Spectre.Console/).

Terminal.Gui: [Official site](https://gui-cs.github.io/Terminal.Gui/), [What's new in v2](https://gui-cs.github.io/Terminal.Gui/docs/newinv2.html), [Multitasking](https://gui-cs.github.io/Terminal.Gui/docs/multitasking.html), [repo](https://github.com/gui-cs/Terminal.Gui), [migration guide](https://github.com/gui-cs/Terminal.Gui/blob/v2_develop/docfx/docs/migratingfromv1.md), [NuGet](https://www.nuget.org/packages/Terminal.Gui), [NuGet 2.4.6 (net10 target)](https://www.nuget.org/packages/Terminal.Gui/2.4.6), [Issue #145](https://github.com/gui-cs/Terminal.Gui/issues/145), [Issue #155](https://github.com/gui-cs/Terminal.Gui/issues/155), [Showcase](https://gui-cs.github.io/Terminal.Gui/docs/showcase.html).

Other frameworks: [Consolonia](https://github.com/Consolonia/Consolonia), [Consolonia QuickStart](https://github.com/Consolonia/Consolonia/wiki/QuickStart-from-scratch), [Console Virtual Terminal Sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences).

Icons / encoding: [Console.OutputEncoding](https://learn.microsoft.com/en-us/dotnet/api/system.console.outputencoding), [EncoderReplacementFallback](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoderreplacementfallback), [Console code pages](https://learn.microsoft.com/en-us/windows/console/console-code-pages), [Nerd Fonts](https://www.nerdfonts.com/), [Nerd Fonts PUA code points](https://github.com/ryanoasis/nerd-fonts/wiki/Glyph-Sets-and-Code-Points), [Cascadia Code 2404 NF](https://devblogs.microsoft.com/commandline/cascadia-code-2404-23/), [terminal #18528 (NF not bundled)](https://github.com/microsoft/terminal/issues/18528), [terminal #8970 (emoji width)](https://github.com/microsoft/terminal/issues/8970), [discussion #13724 (emoji presentation)](https://github.com/microsoft/terminal/discussions/13724).

Examples: [spectreconsole/examples](https://github.com/spectreconsole/examples), [spectreconsole.net](https://spectreconsole.net/), [Terminal.Gui UICatalog](https://github.com/gui-cs/Terminal.Gui), [btop](https://github.com/aristocratos/btop), [lazygit](https://github.com/jesseduffield/lazygit).

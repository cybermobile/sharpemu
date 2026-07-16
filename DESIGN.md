<!--
SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Design

## Source of truth

- Status: Active
- Last refreshed: 2026-07-16
- Primary product surfaces: Desktop game library, launch controls, emulator options, environment/debug options, and runtime console.
- Evidence reviewed: `README.md`, `src/SharpEmu.GUI/App.axaml`, `src/SharpEmu.GUI/MainWindow.axaml`, `src/SharpEmu.GUI/MainWindow.axaml.cs`, `src/SharpEmu.GUI/Languages/*.json`, `assets/images/*`, RPCS3's Qt main window/settings/game-list sources, and PCSX2's Qt game-list/settings sources plus its 2.0 and 2.6 UI retrospectives.
- Assumption: Until user research says otherwise, prioritize emulator developers and compatibility testers while keeping the common select-and-launch path approachable.

## Brand

- Personality: Technical, focused, calm, and credible; experimental without feeling unfinished.
- Trust signals: Clear runtime status, explicit experimental language, readable logs, predictable native controls, and visible project provenance.
- Avoid: Excessive neon glow, game-console imitation that obscures desktop conventions, emoji as functional icons, unexplained abbreviations, and decorative motion during debugging.

## Product goals

- Goals: Make a game easy to find and launch; make emulator state obvious; make diagnostic settings understandable and reversible; keep logs available without dominating the library.
- Non-goals: A storefront, social launcher, cover-art browser, or full-screen console-shell replacement.
- Success signals: The primary launch flow is obvious without documentation; all controls are keyboard reachable; selected, focused, loading, empty, running, stopped, and error states are distinguishable without color alone.

## Personas and jobs

- Primary personas: Emulator contributors, compatibility testers, technically confident players, and issue reporters.
- User jobs: Add or open a title, identify the selected build, start/stop emulation, adjust a small set of launch options, capture useful logs, and reproduce a failure.
- Key contexts of use: Long desktop debugging sessions, keyboard/mouse navigation, controller navigation from a couch, and macOS operation through Rosetta 2.

## Information architecture

- Primary navigation: The library is the default desktop workspace. The compact top command bar leads with context-sensitive library search and places Library and Settings immediately to its right. File operations live in the native File menu, with Add Folder repeated only as empty-library recovery; settings use a category rail inside their own workspace rather than consuming permanent main-window width.
- Core routes/screens: Library grid, library empty/search/loading states, General options, device-specific Controls/input mapping, Environment/debug options, selected-game action bar, and collapsible console.
- Content hierarchy: Native window/menu context first; library search and workspace switching second; library content third; selected-title metadata and the primary launch action in a distinct bottom inspector; diagnostics and emulator/build status last. Controls asks for the active input device first and shows only that device's settings.

## Design principles

- Desktop first, console friendly: Follow desktop window, keyboard, and accessibility conventions while retaining optional gamepad shortcuts.
- State is never color-only: Pair color with text, outline, shape, or iconography for selection, focus, success, warning, and running state.
- Progressive disclosure: Keep compatibility/debug controls and console output available but outside the primary launch path.
- Separate play from tuning: Keep the common library-and-launch path stable; present global settings as a dedicated workspace and make per-game overrides explicit when they are introduced.
- Safe input recovery: Controller and keyboard mappings always expose defaults, avoid capture-only dead ends, and remain usable without a connected controller. Fullscreen always provides an on-screen exit plus Escape and F11 shortcuts, with Control-Command-F also supported on macOS.
- Two interaction modes, not one compromise: Retain a compact desktop UI now; a future controller-first or Big Picture surface should be purpose-built rather than stretching desktop navigation into a console shell.
- Quiet hierarchy: Use spacing, type weight, and restrained surfaces before glow, gradients, or heavy borders.
- Tradeoffs: Preserve existing behavior and localization wiring before introducing richer navigation or a new component framework.

## Visual language

- Color: Near-black neutral surfaces with cool blue-violet accent, readable cool-gray secondary text, and reserved semantic red/green. Normal text targets at least 4.5:1 contrast.
- Typography: Platform-appropriate sans serif for UI and a legible monospaced stack for logs and environment keys. Avoid text below 12 px except compact metadata.
- Spacing/layout rhythm: 4 px base rhythm; 8/12/16/24/32 px are preferred. Main content is capped for scanability and reflows rather than stretching controls.
- Shape/radius/elevation: 8 px controls, 12 px cards, restrained one-pixel borders, and one subtle shadow level for floating/selected content.
- Motion: 120–200 ms state transitions only. Motion must not be required to understand state and should remain subtle during debugging.
- Imagery/iconography: Reuse the SharpEmu mark and game artwork. Functional controls use text or consistent vector symbols, not platform-dependent emoji. Controller setup uses the user-supplied, front-facing controller image on a transparent background; it never substitutes for labeled controls.

## Components

- Existing components to reuse: Avalonia Fluent controls, card and pill styles, library tile `ListBox`, native `TabControl`, launch bar, and console panel. Button and toggle-button content stays centered on both axes across every workspace.
- New/changed components: Desktop title/menu chrome, compact command toolbar, clear active workspace treatment, settings category rail, a Controller/Keyboard segmented mode switch, controller/keyboard binding rows, a transparent controller reference image, reset-to-defaults action, consistent control heights, visible focus rings, accessible search/toolbar metadata, improved empty state, larger artwork grid, a visually distinct selected-game inspector, and a high-visibility fullscreen exit beside the workspace controls.
- Variants and states: Default, pointer-over, pressed, keyboard-focus, selected/checked, disabled, loading, empty, error, running, and stopped.
- Token/component ownership: Shared color, typography, shape, control-size, and state tokens live in `App.axaml`; page composition remains in `MainWindow.axaml`.

## Accessibility

- Target standard: WCAG 2.2 AA principles where applicable to desktop UI, plus Avalonia automation semantics on macOS, Windows, and Linux.
- Keyboard/focus behavior: Every action is reachable by Tab/Shift+Tab; focus-visible receives a two-pixel accent indicator; Enter/Space activate standard controls; existing shortcuts remain discoverable through automation metadata. Escape exits fullscreen and F11 toggles it even when a child control has handled the key. Focusable controls inside custom window chrome always keep their pointer input; only non-interactive title-bar space may initiate a window drag.
- Contrast/readability: Normal text targets 4.5:1, large text and component boundaries 3:1, and secondary text is never communicated by opacity alone.
- Screen-reader semantics: Name ambiguous/icon controls, associate fields with labels or help text, mark decorative imagery as raw, announce dynamic status politely, and expose stable automation IDs for primary actions. The controller image has concise alternative text but remains supplementary; every binding stays available through the labeled mapping list.
- Reduced motion and sensory considerations: Keep motion brief and nonessential; avoid flashing and continuous animation outside an indeterminate progress state.

## Responsive behavior

- Supported breakpoints/devices: Desktop windows from 980 px wide through large maximized displays; Windows, Linux, and macOS.
- Layout adaptations: The library uses a reflowing wrap panel; content width is capped; toolbars wrap or condense before controls clip; options remain scrollable. General, Controls, and Environment share one stable settings-workspace width and scrollbar gutter so category changes never move the rail or card edges. The input-device switch stays above the active mode, and the controller image scales proportionally without horizontal scrolling.
- Touch/hover differences: Interactive targets are at least 36 px high on desktop; hover is supplemental and never the sole state indicator. macOS fullscreen retains native system chrome so moving the pointer to the screen top can reveal the platform window controls; the in-content exit button remains available without hover.

## Interaction states

- Loading: Preserve context, show a concise label and indeterminate progress without blocking unrelated navigation.
- Empty: Explain why the space is empty and offer one primary recovery action.
- Error: Use specific plain-language status text, retain logs, and avoid terminating the launcher for recoverable failures.
- Success: Confirm scans, copies, and launches in the status region without modal interruption; remapping changes apply to the next game launch and reset updates the active mode immediately. Switching input modes changes presentation only and never discards mappings.
- Disabled: Keep the control visible, lower emphasis, and make the prerequisite clear from nearby text.
- Offline/slow network: Core library and launch flows do not require a network; external community links fail without affecting emulator use.

## Content voice

- Tone: Direct, factual, concise, and helpful to technically literate users.
- Terminology: Use “game,” “library,” “launch,” “console,” and “environment variable” consistently; retain exact API/environment names where diagnostic precision matters.
- Microcopy rules: Lead with the action, describe consequences, avoid unexplained status codes, and use sentence case except exact identifiers.

## Implementation constraints

- Framework/styling system: Avalonia Fluent on .NET 10 with XAML styles and C# code-behind.
- Design-token constraints: Extend existing resources in `App.axaml`; do not add a second theme package or new dependency.
- Performance constraints: Avoid per-frame UI allocations, oversized blurred layers, and artwork effects that compete with emulation/rendering workloads.
- Compatibility constraints: Preserve localization keys, controller navigation, macOS main-thread window behavior, Rosetta-supported `osx-x64` publishing, and the existing single-executable release shape.
- Input constraints: The first remapping surface is global and dropdown-based. It shows either Controller or Keyboard controls at a time, defaults to Controller, and treats the controller image as orientation rather than an interactive capture surface. Per-game profiles, multiple guest pads, and macOS press-to-bind capture remain later capabilities because controller events currently live in the separate presenter process.
- Test/screenshot expectations: XAML must compile in Debug and Release; unit tests and REUSE lint must pass; the macOS app must launch and expose the expected accessibility tree. Visual screenshot capture is attempted when host permissions allow it.

## Open questions

- [ ] Confirm whether the long-term primary audience is contributors/testers or end users; this changes how prominently diagnostic controls should appear.
- [ ] Decide whether controller hint chips should be always visible, shown only after controller input, or configurable.
- [ ] Confirm whether a native `.app` bundle and notarization are release goals for macOS.

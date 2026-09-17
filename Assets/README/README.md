# README product imagery

The source captures were taken from the installed CodexMeter 2.0.9 (build 20009) app on macOS on 2026-09-11 through native computer-use screenshot capture.

- `notch-expanded-capture.png`: original, unmodified app-window screenshot (335 × 1134 pixels).
- `notch-collapsed-capture.png`: original, unmodified resting-state capture.
- `notch-live.png`: product crop, rectangle `(248, 410, 335, 742)` in the expanded capture. No UI, readings, colors, or provider marks were redrawn.
- `codexmeter-notch.png`: a white README banner based on the original notch composition, with an AI-assisted image edit on 2026-09-15 to describe the 11 supported providers. The selected copy is “11 providers. One place.” with Codex, Claude Code, GitHub Copilot, Cursor, and more. The two rings are illustrative examples based on the original capture-time 32% and 66% readings; this edited banner is not an unmodified app screenshot or a current quota report.
- `download-macos.svg`: a locally authored download link graphic.

The capture used the existing Right / Medium / Blue / Remaining choices. Always show was enabled only for the expanded capture, then restored to Show on hover. No account identifier, session title, terminal, or unrelated app content is included in the product crop. The original capture files remain unmodified. The banner's AI-assisted revision does not use Codenotch's screenshots.

The older `codexmeter-hero.png` and `codexmeter-screenshot.png` are retained for historical links but are no longer the README's product image.

## Quick controls animation

`coderim-controls.gif` shows native SwiftUI renders of the actual notch controls opening below and above the same notch. Captured on 2026-09-17 with illustrative Codex and Claude Code readings (32% and 66% remaining), without personal accounts or desktop content. The 400 × 1000 pixel GIF contains 250 frames at 25 fps, loops for 10 seconds, and was encoded with FFmpeg. No explanatory text is drawn into the animation.

## CodeRim 2.1.0 branding

`coderim-notch.png` is the 2026-09-17 AI-assisted edit of the historical banner using the built-in imagegen tool. It changes the product heading to “CodeRim” and the provider count to “70 providers. One place.” The remaining composition and illustrative 32%/66% readings are retained. It is a marketing graphic, not a current quota report. Original CodexMeter imagery remains available under its historical filenames.

## CodeRim 2.1.1 Open Rim logo

The user selected the Open Rim concept from the built-in imagegen comparison board created on 2026-09-17 (`exec-39cf04ae-6646-4bb3-b339-8f699a0390a4.png`). The production mark is a locally authored vector construction, not a crop of that raster preview or a third-party provider logo. `Sources/CodeRim/App/CodeRimMark.swift` shares its ring geometry with the native menu-bar template image and `Scripts/generate_brand_assets.swift`. That script generates `Assets/AppIcon.svg`, `Assets/AppIcon-1024.png`, and all ten `Assets/AppIcon.iconset` PNGs; macOS `iconutil` packs them into `Assets/AppIcon.icns`. The command is documented at the top of the script. The original product screenshots and banner are unchanged.

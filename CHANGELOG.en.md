# DiskLens changelog

[简体中文](CHANGELOG.md) · [Website & release archive](https://disklens.site/#releases)

## 1.1.0 — 2026-11-07

This sharing update remains 1.1.0. Existing builds with the same version require a manual download and reinstall; no same-version update prompt appears. Sharing defaults to a separate HTTPS Beta, requires separate consent and may be rate-limited.

- Add shared experience (test), off by default: reuse requires verified software identity, major version, directory role and a complete allowlisted structure. Unmatched directories still need your own API.
- Enable only after reading the agreement, consenting to future contributions and previewing scope. History is a separate choice; exclude folders, review contributions and withdraw one or all.
- Use local protection → valid personal cache → shared experience → personal AI, with source labels, reanalysis and deletion confirmation.
- Consensus requires 5 eligible installation identifiers, 72 hours of observation and 90% agreement; identifiers do not prove independent people. Current cloud experience: 0; eligible entries among 437 audited legacy analyses: 0.
- Invalidate cache by full context; expire 30 days from original analysis without extending on reuse. Cap at 20,000 entries / 32 MiB, coalesce concurrent calls and report write failures.

- Analyze the scan root or the current real directory and its descendants.
- Show processed items, activity, elapsed time and cancellation while preparing candidates and calling the model in the background.
- Report API Token usage and estimated prices for verified official DeepSeek models. Unknown pricing is not presented as free.
- Reuse unchanged analysis entries within a scan; invalidate changed files and their ancestors while preserving other folders' results.
- Automatically monitor external file additions, deletions, renames and size changes. Coalesce events and update affected blocks and sizes; defer while AI is busy and resume when returning to the app.
- Add a Windows x64 installer while retaining the portable ZIP.
- From 1.1 onward, check for newer releases at startup with concise notes and actions to dismiss, skip that exact version or visit the website. Version 1.0 has no update checker and requires one manual upgrade.

[Installer](https://assets.disklens.site/releases/1.1.0/DiskLens-Setup-1.1.0-x64.exe) · [Portable ZIP](https://assets.disklens.site/releases/1.1.0/DiskLens-1.1.0-win-x64.zip) · [Release & checksums](https://github.com/kxdyh/Disklens/releases/tag/v1.1.0)

A later user-provided deepseek-flash billing screenshot showed about CNY 0.11 for 14:00–15:00. Per-request reconciliation was unavailable; the following historical estimates are not actual charges.

Historical AI usage and price estimate: DeepSeek Flash, medium thinking, Token-saving mode and a 1 GiB candidate threshold. Three drives produced 433 new candidates and 88,831 Tokens, estimated at ¥0.30–0.59 using the official off-peak / peak rates at testing time. This is not a fixed price per disk size; actual charges depend on the provider. [Method](README.en.md#what-did-real-ai-analysis-cost)

## 1.0.0 preview — 2026-11-06

Original release tag: `v2026.11.06-preview`, application build `ea80630`. Its published assets remain available and are not replaced by this update.

- Rectangular disk space visualization, folder navigation and pages of 50 items.
- Up to 3 concurrent scans, with separate tabs, live progress and done states.
- Up, scan-root navigation and scan reports in normal and full-screen focus views.
- Read errors and causes, Windows system-reserved usage, alternate data stream accounting and deduplicated errors.
- Light / dark themes, optional DeepSeek / OpenAI-compatible analysis and Token budgets.
- Prevent collapsed Settings dropdowns changing with the mouse wheel.

[Archived ZIP](https://github.com/kxdyh/Disklens/releases/download/v2026.11.06-preview/DiskLens-win-x64-latest.zip) · [Original release](https://github.com/kxdyh/Disklens/releases/tag/v2026.11.06-preview)

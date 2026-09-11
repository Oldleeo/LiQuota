# Changelog

All notable changes to LiQuota are documented here.

## [3.0.1] - 2026-09-11

### Changed

- Replaced the letter-based icon with a wordless dual-quota-ring symbol.
- Added an Apple-inspired dark squircle, restrained depth, and multi-resolution Windows ICO assets.
- Removed the `L` glyph from the emergency tray-icon fallback as well.

## [3.0.0] - 2026-09-11

### Added

- Automatic current-account detection through `account/read`
- Privacy-masked account identity in the details popup
- Live refresh on account and rate-limit update notifications
- System/light/dark appearance modes
- Pixel-level docking adjustment saved per user
- Privacy-safe quota summary copy action
- Optional low-quota notifications at 20%
- Multi-window scroll layout and remaining-quota progress bars
- Windows x64/ARM64 build and release workflows
- Dependency-free parser smoke tests and open-source project documentation

### Changed

- Popup right edge now aligns with the badge instead of projecting beyond it
- Product metadata and startup registry name now use LiQuota
- QA metadata no longer stores a full account email

## [2.2.0] - 2026-09-10

- Introduced the LiQuota name and flat application icon.
- Added dynamic quota-window labels and reset-credit display.

## [2.0.1] - 2026-09-07

- Added Codex executable discovery through running Codex/ChatGPT processes.

## [2.0.0] - 2026-09-07

- Added compact title-bar docking and official App Server quota reading.

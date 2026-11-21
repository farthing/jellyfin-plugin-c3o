# Changelog

Versions are the plugin's four-part Jellyfin version. Each one is tagged `v<version>` and has a
release with the package attached.

## 0.1.1.0 (2026-11-22)

First public release.

- Nightly task that finds movies and TV episodes with no SRT subtitle in your preferred languages (up to five, in order of preference) and downloads one through the subtitle providers already set up in Jellyfin.
- Settings page with language priority, library selection, matching rules, per-run limits, a dry run mode and a status view of the last run and recent downloads.
- Stops cleanly at the provider's daily download limit and continues on the next run.

## 0.1.0.0 (2026-11-21)

First build, not published.

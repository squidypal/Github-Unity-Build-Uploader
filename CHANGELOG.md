# Changelog

All notable changes to this package will be documented in this file.

## [1.0.0]

- Initial package release.
- Zips the build output directory after a successful Unity player build.
- Creates a GitHub release tagged `build-<platform>-<timestamp>` (marked prerelease) and uploads the zip as an asset.
- Editor window at `Tools > Build Uploader` for configuring the GitHub token and repo slug.
- Migrates settings from the legacy `DiscordBuildUploader.*` EditorPrefs keys on first run.

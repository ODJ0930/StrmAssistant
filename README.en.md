# Strm Assistant Lite Compatibility Build

This is a personal compatibility build based on the original project [sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant).

This repository only documents the compatibility changes made for newer Emby Server versions. The original author's work, project structure, and license are respected. For the full feature list, documentation, and update history, please refer to the upstream repository.

## Changes In This Build

- Added compatibility for internal API changes in Emby Server 4.9.3.0 and 4.10.0.14.
- Adapted changing signatures for `GetStaticMediaSources`, media mounting, external subtitle scanning, fingerprint processing, and thumbnail refresh through reflection.
- Adjusted build settings so the plugin can be built inside a Linux .NET SDK container while preserving the original Windows PostBuild behavior.
- The Release provides a single `StrmAssistantLite.dll` file with the required dependency merged for simpler deployment.

## Verified Environments

The plugin was tested with:

- `amilys/embyserver:4.9.3.0`
- `emby/embyserver:4.10.0.14-debug-amd64`

Verified areas:

- Plugin loading and configuration page registration
- `Extract MediaInfo`
- `Persist MediaInfo`
- `Scan External Subtitles`
- `Merge Multi Versions`
- Test media scan with `mp4 + srt + strm`

## Installation

1. Download `StrmAssistantLite.dll` from Releases.
2. Copy it into the Emby Server `plugins` directory.
3. Restart Emby Server.
4. Confirm that `Strm Assistant` is loaded on the Emby plugins page.

## Original Work

The original work of Strm Assistant belongs to the upstream author and contributors. This repository only adds compatibility changes for a personal use case. It does not claim ownership of the original project and does not replace the upstream release.

For full functionality, licensing, usage notes, and official support, please visit:

[https://github.com/sjtuross/StrmAssistant](https://github.com/sjtuross/StrmAssistant)

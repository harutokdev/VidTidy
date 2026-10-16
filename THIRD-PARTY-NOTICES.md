# Third-party notices

VidTidy itself is released under the [MIT License](LICENSE). The Windows release (`VidTidy-<version>-win-x64.zip`) also contains the components below, each under its own license.

| Component | Version | License | Copyright |
|---|---|---|---|
| .NET runtime and Windows Desktop runtime (WPF), included because the release is self-contained | 10.0 | MIT ([dotnet/runtime](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), [dotnet/wpf](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)); third-party code inside the runtime: [THIRD-PARTY-NOTICES](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) | .NET Foundation and Contributors |
| Microsoft.Data.Sqlite | 10.0.11 | MIT | Microsoft Corporation |
| SQLitePCLRaw (core, provider, bundle) | 2.1.12 | Apache-2.0 | SourceGear, LLC |
| SQLite (`e_sqlite3`, via SQLitePCLRaw.lib.e_sqlite3 3.53.3) | 3.x | Public domain ([sqlite.org/copyright](https://sqlite.org/copyright.html)) | — |
| Google.Protobuf | 3.35.1 | BSD-3-Clause | Google Inc. |
| Grpc.Net.Client, Grpc.Net.Common, Grpc.Core.Api | 2.83.0 | Apache-2.0 | The gRPC Authors |

## Not included, downloaded separately

- **FFmpeg** — not part of the release. `Install-VidTidy.ps1` downloads the LGPL build (`ffmpeg-n8.1-latest-win64-lgpl-8.1.zip`) directly from its publisher, [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), checks it against that release's `checksums.sha256`, and places `ffmpeg.exe`, `ffprobe.exe` and FFmpeg's `LICENSE.txt` in `runtime\ffmpeg\`. FFmpeg is licensed under the LGPL 2.1 or later; source code is available at [ffmpeg.org](https://ffmpeg.org/download.html). VidTidy runs FFmpeg as a separate program and does not link to it.
- **Python runtime and AI models** (optional semantic search) — not included and not downloaded by the installer. See [docs/license-engineering-dossier-llm-jp-clip.md](docs/license-engineering-dossier-llm-jp-clip.md) for the model's license notes.

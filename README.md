# RemotePlay

RemotePlay is a Windows x64 media library and playback application. It combines
a WPF desktop app, a browser-based remote interface, and an isolated VLC video
player process.

## Supported environment

- Windows x64.
- .NET 10 SDK (10.0.402 was used for the repository baseline); the application
  targets `net8.0-windows`.
- Deno 2.x is optional and is only needed for the browser regression test.
- FFmpeg and FFprobe on `PATH` are optional; they are used for browser video
  compatibility conversion and media diagnostics.

The `PortableWinX64` publish is framework-dependent and requires the .NET 8
Windows Desktop Runtime on the target machine. `SelfContainedWinX64` includes
the main app's .NET runtime. Both profiles include the x64 LibVLC runtime and
the isolated video player; distribute the complete publish directory.

## Build, test, and run

Run these commands from the repository root in PowerShell:

```powershell
dotnet build .\RemotePlay.slnx --configuration Release
dotnet test .\RemotePlay.Tests\RemotePlay.Tests.csproj --configuration Release
deno test --allow-read=WebAssets/app-live-tv.js .\RemotePlay.Tests\WebAssets\app-live-tv.browser.test.mjs
dotnet run --project .\RemotePlay.csproj
```

The Deno command is optional when Deno is not installed. `remoteplay.example.json`
is a sanitized configuration reference, not a replacement for the app's
per-user configuration.

## Publish

From PowerShell, run the portable (framework-dependent) x64 publish:

```powershell
.\build\Publish-RemotePlay.ps1 -Profile PortableWinX64
```

To include the main app's .NET runtime, use:

```powershell
.\build\Publish-RemotePlay.ps1 -Profile SelfContainedWinX64
```

The validated publish output is `bin\Release\net8.0-windows\publish`.

## Documentation

- [Certificate help guide](docs/user-guide/CERTIFICATE_HELP_INSTRUCTIONS.md)
- [Isolated video player testing guide](docs/testing/VIDEO_PLAYER_ISOLATION_TESTING.md)
- [Active plans and suggestions](docs/plans/)
- [Completed plans and suggestions archive](docs/archive/)
- [Repository cleanup plan](docs/plans/REPOSITORY_CLEANUP_PLAN.md)

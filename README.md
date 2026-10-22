# DAGNUE Desktop Assistant

DAGNUE is a native, Codex-powered Windows desktop assistant. It is built with C#, WinUI 3, the Windows App SDK, and a local Codex app-server bridge. It does not use Electron, a browser shell, or a hosted web UI.

## Current capabilities

- Animated neural-core visualization with honest system telemetry
- Local Codex threads, streaming responses, cancellation, and interactive approvals
- Windows notifications, sign-in startup registration, and a native system-tray icon
- Persistent reminders and reviewable routines
- Push-to-talk Windows speech recognition
- Safe application launching with configurable permission modes
- Local SQLite memory, conversation history, settings, and audit ledger
- Explicit active-window and clipboard context attachment
- MCP connector discovery through Codex
- Windows 10 build 19041+ compatibility (tested on build 19045)

## Prerequisites

- Windows 10 version 2004/build 19041 or newer, or Windows 11
- Visual Studio 2022 with Desktop development with C++ and .NET desktop development
- .NET 9 SDK
- Codex CLI installed and signed in (`codex login status`)

## Build and test

```powershell
.\scripts\build.ps1
```

The self-contained test build is written to `artifacts\portable`. It includes the Windows App SDK runtime and can run without Developer Mode.

## Run locally

```powershell
.\artifacts\portable\Nexus.App.exe
```

`DAGNUE.exe` in the repository root is the permanent launcher. Every successful
`Nexus.App` build automatically refreshes `artifacts\portable`, so shortcuts should
point to the root `DAGNUE.exe` and do not need to be recreated after future builds.

The packaged build provides startup registration and full notification activation. The portable build is intended for local testing.

## Produce an MSIX

Create or supply a code-signing PFX whose subject matches `CN=NEXUS Development`, then run:

```powershell
$env:NEXUS_CERT_PASSWORD = "your-pfx-password"
.\scripts\package-msix.ps1 -CertificatePath C:\secure\nexus.pfx
```

See [docs/SECURITY.md](docs/SECURITY.md) before enabling broad permissions or connectors.

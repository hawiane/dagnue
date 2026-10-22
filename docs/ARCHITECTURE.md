# Architecture

## Process model

NEXUS runs as a per-user WinUI 3 desktop process. Closing the main window hides it while the native notification-area icon remains active. The application launches `codex app-server` as a child process and communicates over newline-delimited JSON-RPC through redirected standard input/output.

The application does not run as an administrator or boot-time Windows service. Startup begins at user sign-in through the packaged `StartupTask` manifest extension.

## Components

- `Nexus.App`: WinUI shell, neural-core visualization, tray icon, notifications, startup, voice, and explicit Windows context access.
- `Nexus.Core`: Codex protocol client, SQLite repository, scheduling, permission policy, system telemetry, local tools, and briefings.
- `Nexus.Core.Tests`: deterministic policy, scheduling, and persistence tests.

## Data

Runtime data is stored beneath `%LOCALAPPDATA%\NEXUS`:

- `nexus.db`: settings, reminders, routines, memory, messages, and audit entries
- `nexus.log`: local crash and connection diagnostics

Credentials are not stored in this database. Codex authentication remains owned by the installed Codex client. External connector authentication remains owned by the connector/MCP provider.

## Safety boundary

Codex provides analysis and streamed agent behavior. NEXUS presents app-server approvals and applies a separate local permission policy to native tools. Critical, destructive, credential, payment, security-setting, and administrator actions are never approved unattended.

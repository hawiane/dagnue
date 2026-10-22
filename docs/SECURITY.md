# Security and privacy

NEXUS defaults to **Confirm before acting**.

## Permission modes

- Observe: permits read-only operations.
- Suggest: prepares changes but does not execute them.
- Confirm: requires an interactive decision for state-changing local actions.
- Trusted routines: permits explicitly trusted, non-critical routines while continuing to block high-risk work.

Critical and destructive operations always require direct confirmation. The assistant cannot approve Windows UAC dialogs or administrator authentication.

## Context access

Microphone, active-window, screen-context, and clipboard settings are disabled or explicitly controlled in Settings. Clipboard text is attached only when the user presses the context button and is capped at 4,000 characters. NEXUS does not run an invisible always-listening microphone.

## Prompt injection

Text from files, windows, clipboard, email, web pages, and connectors must be treated as untrusted data rather than instructions. Keep connector access narrow, review side-effecting tool calls, and do not enable unattended full-access Codex configuration.

## MSIX development certificate

Local development packages may be signed with a self-signed certificate. Trust only the public certificate generated for this project, install it in the current user's Trusted People store, and remove it when no longer needed. Production distribution should use a real code-signing certificate or Microsoft Store signing.

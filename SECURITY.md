# Security Policy

## Reporting a vulnerability

Please do not open a public issue containing credentials, tokens, `auth.json`, account IDs or private response data.
Use a private GitHub security report or contact the repository maintainer through the GitHub profile.

## Credential handling

The application reads Codex CLI OAuth credentials only at query time, keeps them in process memory, and sends them only to the fixed HTTPS usage endpoint. It does not persist credentials, print them, or include them in error messages.

The right-click diagnostic report is intentionally redacted: it reports only connection mode and safe status metadata, never tokens, account IDs, proxy addresses or full API responses. Startup is stored as a per-user Windows Run entry and does not require administrator access.

The local settings file contains only refresh, history retention, startup delay, optional startup update notifications, appearance, notification and window-position preferences. Closing the bar hides it to the notification area; the tray controller does not read or persist OAuth data. Balloon notifications contain only a controlled window label and usage percentage.

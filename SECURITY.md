# Security Policy

## Reporting a vulnerability

Please do not open a public issue containing credentials, tokens, `auth.json`, account IDs or private response data.
Use a private GitHub security report or contact the repository maintainer through the GitHub profile.

## Credential handling

The application reads Codex CLI OAuth credentials only at query time, keeps them in process memory, and sends them only to the fixed HTTPS usage endpoint. It does not persist credentials, print them, or include them in error messages.

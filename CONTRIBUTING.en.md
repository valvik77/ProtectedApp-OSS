# Contributing to ProtectedApp

> **English** | [Español](CONTRIBUTING.md)

Thank you for your interest. Before opening a contribution, read
[SECURITY.en.md](SECURITY.en.md): vulnerabilities must be reported privately,
not through public issues.

## Development environment

Windows, the .NET SDK, Windows App SDK, and Dokany are required for mount tests.
Inno Setup and the Windows SDK are required to produce the installer.

```powershell
dotnet restore
dotnet build ProtectedApp.sln -c Release -warnaserror
dotnet test ProtectedApp.sln -c Release --no-restore
```

Do not commit build output, certificates, `.pfx` files, keys, `.env` files,
logs, vaults, encrypted backups, or real test data.

## Changes

- Keep changes small and add tests when altering encryption, IPC,
  authentication, installation, or process controls.
- Maintain Spanish and English localization for every user-visible string.
- Do not weaken signature, ACL, path-validation, or format-limit checks merely
  to make a test pass.
- Describe the behavior, risk, and verification performed in the pull request.

## Publishable builds

Releases must be generated from a tagged revision and pass tests, dependency
analysis, and integrity checks. Code signing is added through the project's
approved signing service; a private key is never stored in the repository or
GitHub artifacts.

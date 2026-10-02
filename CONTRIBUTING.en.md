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

### System integration tests

A normal run reports some tests as **skipped**. That is not a failure: they
verify a real installation (the Guardian service, its SYSTEM task, Dokany, or
Explorer) and there is nothing to assert without one. CI never runs them,
because a GitHub runner has no ProtectedApp installation.

Running them requires a test machine — preferably a disposable virtual machine —
with ProtectedApp already installed, and an **elevated** console:

```powershell
$env:PROTECTEDAPP_RUN_SYSTEM_INTEGRATION_TESTS = '1'
dotnet test ProtectedApp.Guardian.Tests/ProtectedApp.Guardian.Tests.csproj `
  -c Release -p:Platform=x64 --filter 'Category=SystemIntegration'
```

Without the variable, or without elevation, each test states what it is missing
in its skip reason. These tests touch the installed service and its scheduled
task — and may even repair it — so do not run them on a working machine.

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

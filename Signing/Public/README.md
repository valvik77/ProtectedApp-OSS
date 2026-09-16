# Public test-signing material

This directory contains **public certificate data only**. It must never contain
a PFX/P12 file, private key, password, token, or signing credential.

`ProtectedApp-Development-Test.cer` is self-signed and not publicly trusted.
Before installing it, read the complete [English
instructions](../../DEVELOPMENT-CERTIFICATE.en.md) or [instrucciones en
español](../../DEVELOPMENT-CERTIFICATE.md), and run:

```powershell
.\Signing\Test-PublicDevelopmentCertificate.ps1
```

Do not install this certificate merely to browse or build the source code.

# Publicar ProtectedApp

Las versiones públicas solo se publican desde un commit revisado de `main` y
una etiqueta inmutable `vM.m.r` (o `vM.m.r.c`). La etiqueta activa el flujo
**Release candidate**, que compila, prueba, audita y crea un instalador sin
firma para su revisión interna. También genera un SBOM SPDX, un archivo de
hashes SHA-256 y atestaciones de procedencia de GitHub.

El flujo no publica automáticamente una GitHub Release. Hasta que SignPath esté
configurado, no existe una versión binaria pública de ProtectedApp. Sus
artefactos sin firma son únicamente candidatos internos de revisión y no deben
publicarse ni distribuirse. Cuando SignPath Foundation haya aprobado el
proyecto, el flujo de firma debe estar limitado a `main` y a etiquetas de
versión, usar su verificación de origen y requerir la aprobación manual indicada
en la [Code signing policy](CODE-SIGNING-POLICY.md).

## Secuencia de lanzamiento firmada

1. Fusiona cambios revisados en `main` y confirma que CI, CodeQL y la auditoría
   de dependencias estén en verde.
2. Crea y publica la etiqueta `vM.m.r` sobre ese commit. No reutilices ni muevas
   etiquetas de versiones ya publicadas.
3. Revisa los artefactos, los hashes, el SBOM y la atestación del trabajo de
   GitHub Actions. Solicita la firma solo desde ese trabajo verificado.
4. Tras la aprobación manual de firma, comprueba Authenticode, el sello de
   tiempo y que el firmante sea el esperado.
5. Crea la GitHub Release a partir de [la plantilla](.github/RELEASE_TEMPLATE.md),
   adjunta exclusivamente el instalador firmado, el SBOM y el archivo de hashes,
   y enlaza esta guía y la política de firma.

No adjuntes certificados, DLibs, metadatos de firma, PFX, claves, tokens ni
candidatos sin firma a una release pública.

## Verificar una descarga

```powershell
Get-FileHash .\ProtectedApp-Setup-x64-M.m.r.exe -Algorithm SHA256
Get-AuthenticodeSignature .\ProtectedApp-Setup-x64-M.m.r.exe | Format-List
```

El hash debe coincidir con el archivo `.sha256` de la misma release y la firma
debe ser válida. Las atestaciones se pueden verificar con GitHub CLI:

```powershell
gh attestation verify .\ProtectedApp-Setup-x64-M.m.r.exe --owner valvik77
```

## Cuentas de mantenimiento

Toda persona con permisos de escritura en GitHub o aprobación de firma en
SignPath debe usar MFA. Se recomienda una passkey o llave FIDO2 y conservar
los códigos de recuperación fuera del equipo de desarrollo.

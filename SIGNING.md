# Firma de lanzamientos open source

## Migración a un certificado distinto

La actualización automática exige la misma huella y rechaza certificados nuevos,
incluidas sus renovaciones. Mientras no exista una transición autenticada por
el firmante anterior, cambiar de certificado requiere una reinstalación:

1. Guarda y bloquea las bóvedas; exporta la configuración cifrada y guarda los
   contenedores y copias fuera de la carpeta local de ProtectedApp.
2. Verifica que puedes recuperar esas copias antes de desinstalar.
3. Desinstala mediante el desinstalador oficial y la contraseña maestra.
4. Verifica el editor del nuevo instalador, instala y restaura la configuración.

No borres manualmente la huella protegida ni admitas cualquier certificado
válido como sustituto. La integración automática con SignPath queda pendiente
de conocer y aprobar su identidad pública.

Los binarios de desarrollo se pueden firmar con un certificado local solo para
pruebas. No se distribuyen públicamente ni se añaden al repositorio.

## Compilación local firmada para pruebas

El flujo local ejecuta las pruebas, publica los binarios, los firma, crea el
instalador de Inno Setup y firma también el `setup`. La versión se incrementa
automáticamente; no indiques una versión fija salvo que sepas que nunca se ha
usado antes.

En un equipo nuevo, crea una única identidad de desarrollo y confíala solo
para el usuario actual:

```powershell
.\Signing\New-DevelopmentCodeSigningCertificate.ps1 `
  -Subject 'CN=Valvik ProtectedApp Development' `
  -TrustForCurrentUser
```

Después, desde la raíz del repositorio, genera el instalador firmado:

```powershell
.\Build-Installer.ps1 -AllowDevelopmentCertificate -RequireSignature
```

El script detecta el certificado local por su asunto, exige una clave privada
accesible, comprueba que permite firma de código y rechaza cualquier fallo de
firma. El resultado queda en
`artifacts\installer\ProtectedApp-Setup-x64-<versión>.exe`.

Puedes comprobar el resultado sin instalarlo:

```powershell
$setup = Get-ChildItem .\artifacts\installer\ProtectedApp-Setup-x64-*.exe |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-AuthenticodeSignature -LiteralPath $setup.FullName |
  Format-List Status,SignerCertificate,TimeStamperCertificate
Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256
```

El certificado local sirve únicamente para desarrollo y para equipos donde su
parte pública se haya confiado explícitamente. No elimina los requisitos de
Smart App Control, WDAC ni la reputación de Windows, y nunca debe usarse para
una versión pública.

Para lanzamientos públicos, ProtectedApp debe solicitar una suscripción de
firma a SignPath Foundation después de publicar el repositorio y cumplir sus
condiciones para proyectos open source. La integración se configurará en el
repositorio de GitHub desde SignPath, con permisos mínimos y una rama/etiqueta
de release protegida.

## Reglas de seguridad

- No guardar certificados, claves privadas, contraseñas o tokens de firma en
  Git, artefactos de Actions ni variables visibles en registros.
- Firmar exclusivamente artefactos generados por CI desde una etiqueta
  protegida y una revisión revisada.
- Verificar Authenticode, cadena de firma, sello de tiempo y hash SHA-256 antes
  de publicar un release.
- Publicar el instalador, sus hashes y las notas de versión desde GitHub
  Releases; no sustituir archivos bajo una misma etiqueta.

La configuración concreta de SignPath no se incluye hasta que la organización
apruebe la solicitud y proporcione su identificador de proyecto. Así se evita
una plantilla con credenciales ficticias o permisos excesivos.

# Migrar a un certificado de firma públicamente confiable

> Guía interna del autor. Describe cómo pasar del certificado de desarrollo
> autofirmado a un certificado emitido por una CA pública, qué cambia en el
> repositorio y qué deja de ser necesario.

## Por qué

El certificado de desarrollo obliga a cada persona que descarga una versión a
instalar manualmente un certificado en su almacén de usuario, verificar una
huella a mano y aceptar un cambio en la configuración de confianza de Windows.
Esa barrera es, con diferencia, el mayor obstáculo de adopción del proyecto, y
no depende de la calidad del software.

Un certificado de CA pública elimina el paso manual: Windows muestra el nombre
del editor en lugar de «Editor desconocido» y el instalador se ejecuta sin
pedir que el usuario modifique su configuración de confianza.

## Opción recomendada: Certum Open Source Code Signing

- Precio: **69 € brutos**, tarjeta criptográfica y lector incluidos.
- Se emite **solo a personas físicas**. El requisito es demostrar actividad en
  un proyecto open source público, no popularidad ni antigüedad.
- Firma mediante **token hardware** (`cryptoCertum`), no en la nube.

Documentación que solicitan:

1. Fotos del titular sosteniendo su documento de identidad, con ambas caras
   completas y legibles.
2. Una factura de suministro (luz, agua, teléfono) a nombre del titular.
3. La URL del proyecto open source, donde se vea la relación del titular con él.

### Alternativas descartadas y por qué

| Opción | Motivo |
| --- | --- |
| SignPath Foundation | Gratuito, pero exige un proyecto OSS consolidado y con tracción. |
| Azure Artifact Signing | Para solicitantes **individuales** solo está disponible en EE. UU. y Canadá. Como empresa la UE sí entra. |
| Sectigo OV | Alrededor de 220 USD/año, sin ventaja funcional sobre Certum para este caso. |
| DigiCert OV | Alrededor de 439 USD/año. |
| EV (cualquier CA) | Da reputación inmediata en SmartScreen, pero cuesta más y exige validación de organización. |

## Consecuencia importante: la firma deja de poder automatizarse

La tarjeta criptográfica debe estar físicamente conectada, así que **GitHub
Actions no podrá firmar**. El flujo actual ya firma en local con
`Build-Installer.ps1`, de modo que esto no cambia nada en la práctica, pero
descarta mover la firma a CI más adelante sin cambiar de producto.

El flujo de release sigue siendo el descrito en [RELEASING.md](RELEASING.md):
la compilación reproducible y las atestaciones las produce GitHub Actions, y la
firma se aplica después, en local, sobre el commit etiquetado.

## Pasos de la migración

1. **Obtener el certificado** y completar la validación de identidad.
2. **Instalar** el lector, los controladores y `proCertum CardManager`.
3. **Comprobar** que la tarjeta expone el certificado al almacén de Windows:

   ```powershell
   Get-ChildItem Cert:\CurrentUser\My |
     Where-Object { $_.HasPrivateKey } |
     Select-Object Subject, Thumbprint, NotAfter
   ```

4. **Anotar la huella SHA-1** del certificado nuevo.
5. **Compilar y firmar** una versión de prueba. El script detecta solo que el
   certificado no es autofirmado, así que `-AllowDevelopmentCertificate` ya no
   se pasa:

   ```powershell
   ./Build-Installer.ps1 -InstallerVersion <M.m.r> `
     -SigningCertificateThumbprint <HUELLA_NUEVA> `
     -RequireSignature
   ```

   `Get-ProtectedAppSigningCertificate` valida además la cadena de confianza y
   avisa si el certificado caduca en menos de 30 días.

6. **Verificar en una máquina limpia** (una VM sin el certificado de desarrollo
   instalado) que el instalador se ejecuta sin advertencia de editor
   desconocido:

   ```powershell
   Get-AuthenticodeSignature .\ProtectedApp-Setup-x64-<M.m.r>.exe |
     Format-List Status, SignerCertificate, TimeStamperCertificate
   ```

   Este paso es el que realmente demuestra la migración: en el equipo de
   desarrollo el certificado autofirmado ya está en el almacén raíz, así que
   allí todo parece válido incluso sin CA pública.

## Qué cambia en el repositorio

Una vez verificada la primera versión firmada públicamente:

- **README.md / README.es.md** — retirar la advertencia inicial sobre el
  certificado de desarrollo y el aviso de «Before downloading». El proyecto deja
  de necesitar que el lector entienda un procedimiento de confianza antes de
  saber qué hace el programa.
- **DEVELOPMENT-CERTIFICATE.md / .en.md** — mantener como documento histórico,
  marcado como aplicable solo a las versiones `dev-v*` anteriores. No borrarlo:
  las versiones ya publicadas siguen refiriéndose a él.
- **DEVELOPMENT-RELEASE-TEMPLATE.md** — sustituir por una plantilla de versión
  firmada, sin la huella del certificado de desarrollo ni el enlace a su guía.
- **RELEASING.md / .en.md** — la sección «Development/test prereleases» pasa a
  ser la excepción y no la vía habitual.
- **CODE-SIGNING-POLICY.md / .en.md** — registrar la CA, el tipo de validación
  y que la clave reside en un token hardware en poder del autor.
- **SECURITY-MODEL.md / .en.md** — actualizar la sección de integridad de
  versiones, que hoy describe la firma de desarrollo.
- **Signing/Public/** — conservar el certificado de desarrollo público para
  poder verificar versiones antiguas, indicando que ya no se usa.

## Lo que un certificado OV no resuelve

SmartScreen construye reputación por firmante. Las primeras descargas de una
versión firmada con un certificado OV nuevo pueden seguir mostrando un aviso
hasta que se acumule histórico. Solo un certificado EV da confianza inmediata.

La diferencia relevante es otra: con un certificado OV el usuario ve el nombre
del editor y **no tiene que instalar nada ni modificar su configuración de
confianza**. Ese es el paso que hoy se pierde la mayoría de la gente.

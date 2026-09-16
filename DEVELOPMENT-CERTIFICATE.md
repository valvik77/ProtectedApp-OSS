# Certificado público para compilaciones de desarrollo y prueba

> [English](DEVELOPMENT-CERTIFICATE.en.md)

## Aviso esencial

Este certificado es **autofirmado, no está respaldado por una autoridad de
certificación pública y no es un certificado cualificado**. Instalarlo modifica
deliberadamente la confianza de Windows para el usuario actual. Debe utilizarse
únicamente para evaluar compilaciones de desarrollo de ProtectedApp en un equipo
de pruebas, máquina virtual o entorno que puedas recuperar.

No garantiza que una compilación sea segura, estable ni una versión oficial. No
evita que Smart App Control o una política WDAC rechacen el archivo. No lo
instales en equipos administrados sin autorización ni en sistemas donde una
alteración de la confianza sea inaceptable.

La clave privada **no está incluida en GitHub ni en el archivo `.cer`**. Sin esa
clave, el certificado público no permite firmar programas. Sin embargo, mientras
confíes en él, Windows podrá confiar en cualquier ejecutable que haya sido
firmado con la clave privada correspondiente. Retíralo cuando terminen las
pruebas.

## Identidad que debes comprobar

- Archivo: `Signing/Public/ProtectedApp-Development-Test.cer`
- Sujeto y emisor: `CN=Valvik ProtectedApp Development`
- Finalidad: firma de código (`1.3.6.1.5.5.7.3.3`)
- SHA-256 exacto del archivo:
  `AFD8A0ADD54130355D878CFEC7E3590119A9FCB536FDD45EC8DC797F82E65C39`
- Huella X.509 SHA-1 mostrada por Windows:
  `4AD1F2E988F4EFD130DF66A02442B470927B433C`
- Validez: del 21 de agosto de 2026 al 21 de agosto de 2028

SHA-1 se muestra únicamente porque Windows lo usa como identificador del
certificado; la integridad del archivo se comprueba con SHA-256.

## 1. Verificar antes de confiar

Clona el repositorio oficial y ejecuta desde su raíz:

```powershell
.\Signing\Test-PublicDevelopmentCertificate.ps1
```

El resultado debe indicar `VALID DEVELOPMENT/TEST CERTIFICATE - NOT PUBLICLY
TRUSTED`, `HasPrivateKey: False` y exactamente las huellas anteriores. Si existe
cualquier diferencia, no instales el certificado ni ejecutes el setup.

## 2. Instalar deliberadamente para el usuario actual

No uses `LocalMachine`. Los dos almacenes siguientes limitan el cambio al usuario
actual. Lee la advertencia anterior antes de ejecutar:

```powershell
$certificate = Resolve-Path .\Signing\Public\ProtectedApp-Development-Test.cer
Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

Windows mostrará o aplicará el cambio de confianza. Esta operación nunca debe
realizarse silenciosamente desde el instalador de ProtectedApp.

## 3. Verificar cada instalador

Comprueba primero el SHA-256 publicado específicamente para esa compilación y,
después de instalar el certificado, su firma:

```powershell
$setup = '.\ProtectedApp-Setup-x64-VERSION.exe'
Get-FileHash -LiteralPath $setup -Algorithm SHA256
$signature = Get-AuthenticodeSignature -LiteralPath $setup
$signature | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
if ($signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.Thumbprint -ne '4AD1F2E988F4EFD130DF66A02442B470927B433C') {
    throw 'El instalador no está firmado por el certificado de prueba esperado.'
}
```

Una firma válida sólo demuestra que el archivo no ha cambiado desde que lo firmó
quien controla esa clave. Comprueba también que la descarga procede de
`https://github.com/valvik77/ProtectedApp-OSS` y que su versión, commit y hash
coinciden con las notas de la compilación.

## 4. Retirar la confianza al terminar

Ejecuta estos comandos únicamente para la huella indicada:

```powershell
$thumbprint = '4AD1F2E988F4EFD130DF66A02442B470927B433C'
Remove-Item -LiteralPath "Cert:\CurrentUser\TrustedPublisher\$thumbprint" -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$thumbprint" -ErrorAction SilentlyContinue
```

Las aplicaciones ya instaladas no se desinstalan automáticamente, pero sus
firmas dejarán de ser de confianza por esta vía.

## Compromiso, sustitución o caducidad

Si el repositorio publica un aviso de compromiso, deja de ejecutar nuevas
compilaciones, retira inmediatamente el certificado mediante los comandos
anteriores y espera instrucciones. Un certificado autofirmado no dispone de una
revocación pública OCSP/CRL comparable a la de una autoridad comercial.

Una sustitución o renovación tendrá otra huella y **nunca debe aceptarse
automáticamente**. ProtectedApp exige reinstalación al cambiar de identidad de
firma. Guarda y bloquea las bóvedas y conserva copias verificadas antes de
desinstalar o cambiar de canal.

Este canal de pruebas es provisional. Las versiones públicas estables requieren
una identidad de firma reconocida públicamente; este certificado no pretende
sustituirla.

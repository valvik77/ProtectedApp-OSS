# Contribuir a ProtectedApp

Gracias por tu interés. Antes de abrir una contribución, revisa
[SECURITY.md](SECURITY.md): las vulnerabilidades se comunican de forma privada,
no mediante incidencias públicas.

## Preparar el entorno

Se necesita Windows, .NET SDK, Windows App SDK, Dokany para las pruebas de
montaje y, para generar el instalador, Inno Setup y el Windows SDK.

```powershell
dotnet restore
dotnet build ProtectedApp.sln -c Release -warnaserror
dotnet test ProtectedApp.sln -c Release --no-restore
```

No subas resultados de compilación, certificados, archivos `.pfx`, claves,
variables `.env`, registros, bóvedas, copias cifradas ni datos de prueba reales.

### Pruebas de integración con el sistema

Una ejecución normal informa de pruebas **omitidas**. No es un fallo: comprueban
una instalación real (el servicio Guardian, su tarea SYSTEM, Dokany o Explorer)
y no hay nada que afirmar sin ella. La integración continua nunca las ejecuta,
porque un ejecutor de GitHub no tiene ProtectedApp instalado.

Para ejecutarlas hace falta una máquina de pruebas —preferiblemente una máquina
virtual descartable— con ProtectedApp ya instalado, y una consola **elevada**:

```powershell
$env:PROTECTEDAPP_RUN_SYSTEM_INTEGRATION_TESTS = '1'
dotnet test ProtectedApp.Guardian.Tests/ProtectedApp.Guardian.Tests.csproj `
  -c Release -p:Platform=x64 --filter 'Category=SystemIntegration'
```

Sin la variable, o sin elevación, cada prueba explica en su motivo de omisión
qué le falta. Estas pruebas tocan el servicio instalado y su tarea programada
—incluso pueden repararla—, así que no las ejecutes en una máquina de trabajo.

## Cambios

- Mantén los cambios pequeños y con pruebas cuando alteren cifrado, IPC,
  autenticación, instalación o controles de procesos.
- Conserva la localización español/inglés de todo texto visible.
- No reduzcas las comprobaciones de firma, ACL, validación de rutas o límites de
  formatos para hacer que una prueba pase.
- Describe en la propuesta el comportamiento, el riesgo y la verificación
  realizada.

## Compilaciones publicables

Los lanzamientos deben generarse desde una revisión etiquetada y pasar pruebas,
análisis de dependencias e integridad. La firma de código se añadirá mediante el
servicio de firma aprobado para el proyecto; una clave privada nunca se guarda en
el repositorio ni en los artefactos de GitHub.

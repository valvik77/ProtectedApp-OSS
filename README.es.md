# ProtectedApp

> [English](README.md) | **Español**

Aplicación WinUI 3 para proteger aplicaciones de escritorio y bóvedas cifradas
en Windows. Guardian detecta los procesos configurados, impide su uso hasta
validar la contraseña y los inicia de nuevo cuando la autorización es correcta.

La política de versiones, aprobaciones y firma pública está en
[Code signing policy](CODE-SIGNING-POLICY.md).
El alcance, límites y cambios realizados en Windows están documentados en el
[modelo de seguridad](SECURITY-MODEL.md).
El proceso para producir, verificar y publicar versiones está en
[RELEASING.md](RELEASING.md).

El inicio automático (`--background`, `--service-managed` o `--recovered`) es siempre silencioso: mantiene oculto el panel y no solicita crear, recuperar ni introducir la contraseña maestra. Esas acciones se reanudan únicamente cuando el usuario abre ProtectedApp de forma interactiva desde su acceso directo o el icono del área de notificación.

La pantalla **Añadir** muestra aplicaciones de escritorio detectadas en el Registro de Windows y permite buscarlas o seleccionar manualmente archivos `.exe`, `.bat` y `.py`. Cuando se intercepta un programa o script, ProtectedApp mantiene oculto el panel de gestión y muestra únicamente una ventana independiente de contraseña.

## Guía de uso

### Primer uso

1. Abre ProtectedApp manualmente desde Inicio, el acceso directo o el icono de la bandeja.
2. Crea la contraseña maestra. Es necesaria para administrar las reglas, recuperar copias y desinstalar de forma segura.
3. En **Configuración**, confirma que el estado de **Servicio Guardian** sea activo. Guardian es quien aplica la protección incluso con el panel oculto.

El inicio automático de Windows es silencioso: no abre el panel ni solicita la contraseña. La contraseña solo se pide al abrir ProtectedApp manualmente o al intentar iniciar un recurso protegido.

### Proteger una aplicación

1. En **Aplicaciones protegidas**, pulsa **Añadir**.
2. Selecciona una aplicación detectada o usa **Elegir archivo** para un `.exe`, `.bat` o `.py`.
3. Elige si usará la contraseña maestra o una contraseña propia y configura la política de reapertura, cierre automático, inactividad u horario si lo necesitas.

Al ejecutar una aplicación protegida, Guardian detiene su inicio y aparece una ventana de contraseña. Tras validarla, la aplicación se inicia normalmente. El panel principal no necesita permanecer abierto.

### Convertir una carpeta en bóveda

1. En **Bóvedas cifradas**, pulsa **Convertir carpeta** y elige la carpeta de origen.
2. Asigna una contraseña y confirma el nombre y la ubicación del archivo .pavault.
3. Comprueba la nueva bóveda y, solo cuando ya no necesites la copia original, elimina esa carpeta manualmente.

La conversión cifra los datos en un contenedor; no aplica bloqueos NTFS a la carpeta original ni modifica los permisos de OneDrive.

### Crear y usar una bóveda

1. En **Bóvedas cifradas**, crea una bóveda `.pavault` y asigna una contraseña propia.
2. Ábrela en modo **Solo lectura** para consultar archivos o en modo **Editar** para trabajar en una unidad virtual.
3. Al terminar, usa **Guardar y bloquear bóveda**. ProtectedApp cifra los cambios y desmonta la unidad.

Si el equipo se apaga, bloquea la sesión o se interrumpe el proceso con una bóveda editable abierta, ProtectedApp conserva un diario cifrado. La próxima apertura ofrece recuperar el trabajo válido; no deja una carpeta de archivos descifrados en el disco.

En **Configuración → General** se puede elegir una letra preferida para las unidades virtuales. Si ya está ocupada, ProtectedApp conserva los dispositivos existentes y monta la bóveda con otra letra disponible. El menú del icono de bandeja muestra las bóvedas montadas para desmontarlas y hasta cinco bóvedas usadas recientemente para abrirlas sin navegar por el panel. Cada fila de bóveda incluye además un acceso a su historial propio.

### Copias y recuperación

- El archivo `.pavault.bak` es la copia cifrada anterior del contenedor. Se conserva intencionadamente para recuperarlo desde la fila de la bóveda.
- Los archivos temporales con extensión `.v3tmp` solo se usan durante una sustitución atómica. Deben desaparecer al completarse la operación; si queda uno tras una interrupción, no lo elimines antes de comprobar la recuperación desde ProtectedApp.
- **Configuración → Copia cifrada** exporta reglas, preferencias e historial a `.pabackup`. No incluye el contenido de las bóvedas: guarda los archivos `.pavault` por separado.

## Limitaciones importantes

- Para contenido que deba sincronizarse, coloca el archivo **.pavault** dentro de OneDrive u otro servicio de nube. No sincronices una carpeta de trabajo abierta de una bóveda.
- ProtectedApp protege los datos de las bóvedas mediante cifrado autenticado, pero no sustituye BitLocker, las cuentas de Windows ni una frontera de seguridad física frente a un administrador local que controle el equipo.
- Una bóveda admite hasta 1 GB de contenido, 20.000 entradas y 100.000 bloques.

La sección **Bóvedas cifradas** crea e importa contenedores `.pavault` protegidos mediante PBKDF2-SHA256 y AES-256-GCM. El formato PAVLT003 cifra y autentica por separado un índice y bloques de 1 MiB, permite leer intervalos concretos sin descifrar el contenedor completo y envuelve una clave de datos aleatoria con la contraseña de la bóveda. El cambio de contraseña genera una nueva clave de datos y vuelve a cifrar todos los archivos; las copias anteriores conservan la contraseña previa. Cada bóveda tiene contraseña propia y un tiempo de bloqueo entre 1 y 10.080 minutos. Al abrirla se puede elegir **Solo lectura** o **Editar**; ambos modos crean una unidad virtual Dokany y descifran únicamente los datos solicitados. El modo editable conserva sus guardados en un journal PAVLT003 cifrado y, al bloquear, reemplaza el contenedor principal de forma transaccional después de verificarlo. Una sesión interrumpida recupera automáticamente el journal válido después de volver a introducir la contraseña. También se puede editar el nombre, el tiempo y la contraseña o retirar la referencia sin borrar el archivo cifrado.

ProtectedApp guarda y bloquea automáticamente todas las bóvedas abiertas cuando Windows bloquea, desconecta o cierra la sesión y también inicia el cierre ante suspensión, hibernación, apagado o reinicio. Las solicitudes simultáneas se procesan una sola vez y el resultado queda en **Actividad**. Cada cierre de archivo en la unidad editable actualiza un journal cifrado; si Windows finaliza el proceso antes del commit definitivo, la siguiente apertura recupera el contenedor cifrado más reciente. El cierre durante suspensión o apagado continúa siendo de mejor esfuerzo, pero no deja una carpeta con archivos descifrados.

La propia sección de bóvedas incorpora un gestor de recuperación. Detecta trabajos pendientes y aperturas incompletas aunque el proceso anterior no pudiera guardar el aviso, y muestra su ruta completa, número de elementos, tamaño y última modificación. **Guardar y bloquear** comprueba primero límites, espacio libre, permisos de escritura y ausencia de enlaces o puntos de montaje; después valida la contraseña de la bóveda, reemplaza el contenedor de forma atómica y elimina el trabajo solamente tras completar el cifrado. También permite abrir los archivos para revisarlos. El descarte permanente exige escribir `DESCARTAR`, autenticar de nuevo la contraseña maestra y superar las mismas validaciones de ruta; los trabajos huérfanos nunca se asocian automáticamente a un contenedor distinto.

Cada guardado que sustituye un contenedor existente conserva además su versión cifrada anterior como `.pavault.bak`. El botón de recuperación de cada fila muestra ambas rutas, tamaños, fechas y el resultado de la inspección estructural. La contraseña de la bóveda autentica criptográficamente la copia antes de cualquier restauración. Si el principal está dañado, se conserva como `.corrupt-fecha.pavault`; una reversión voluntaria de un principal válido lo conserva como `.replaced-fecha.pavault`. La sustitución usa una copia temporal escrita a disco y dos verificaciones antes y después del movimiento. También puede eliminarse una copia antigua con confirmación y contraseña maestra. Al abrir o importar, si el principal falla pero la copia acepta la contraseña, ProtectedApp ofrece recuperarla sin registrar erróneamente un fallo de contraseña. Si el principal ya no existe, **Importar** permite seleccionar directamente su archivo `.pavault.bak` para reconstruirlo.

PAVLT003 mantiene compatibilidad de lectura con PAVLT002. Una bóveda antigua utiliza una única vez el modo de compatibilidad para migrarse al formato segmentado al bloquearla correctamente, conservando el contenedor anterior como `.pavault.bak`; desde entonces la edición es completamente virtual. Los montajes utilizan Dokany 2.3.1.1000, que el instalador verifica e incorpora automáticamente cuando falta; no se elimina al desinstalar ProtectedApp porque puede ser compartido por otros programas. El contenedor admite hasta 1 GB de contenido, 20.000 entradas y 100.000 bloques. Las copias de seguridad de ProtectedApp incluyen sus referencias y preferencias, pero no duplican el contenido de los `.pavault`; esos archivos deben respaldarse por separado.

La ventana principal se puede redimensionar y maximizar. Sus vistas admiten desplazamiento para conservar el acceso a todos los controles con escalado de texto alto o pantallas pequeñas. La cabecera personalizada distingue **Minimizar**, que conserva el panel abierto en la barra de tareas, de **Cerrar**, que bloquea la sesión de gestión y oculta ProtectedApp. El botón etiquetado **Bloquear**, visible junto al estado de protección, ofrece **Bloquear ahora** con confirmación: revoca todos los periodos de confianza, rearma Gate, cierra las aplicaciones y scripts protegidos que sigan ejecutándose y vuelve a ocultar el panel. El candado inferior conserva el mismo acceso. El número de procesos finalizados queda registrado en Actividad; se advierte previamente porque las aplicaciones podrían contener trabajo sin guardar.

En **Configuración → Bloqueo automático del panel** se puede elegir Nunca, 1, 5, 15 o 30 minutos. Al vencer un periodo sin actividad de teclado o ratón, ProtectedApp oculta únicamente la interfaz de gestión y vuelve a exigir la contraseña maestra; no cierra aplicaciones protegidas ni altera sus autorizaciones. Los diálogos abiertos pausan este contador para evitar cerrar una edición en curso.

El interruptor **Bloquear al cerrar o salir**, activado de forma predeterminada, decide si un cierre normal de la ventana invalida también la sesión de gestión. Al desactivarlo, cerrar solo oculta el panel y permite recuperarlo sin otra contraseña mientras la sesión siga abierta. Esta preferencia no afecta al botón **Bloquear**, al vencimiento por inactividad ni a la respuesta antimanipulación, que siempre bloquean la interfaz.

Cada fila de la lista incorpora además su propio candado entre **Editar** y **Eliminar**. Este bloqueo selectivo revoca solamente la autorización de esa regla y cierra sus procesos, sin ocultar el panel ni interrumpir las demás aplicaciones protegidas. En scripts Python que comparten intérprete se conserva la autorización técnica del host mientras otra regla siga ejecutándose, pero se elimina la sesión y los procesos pertenecientes al script elegido.

Cada regla permite decidir cuándo volver a pedir la contraseña: al cerrar la aplicación (valor seguro predeterminado), después de 5 o 15 minutos, o después de 1 hora. El periodo comienza únicamente tras validar la contraseña y los relanzamientos realizados dentro de él no amplían su duración.

Opcionalmente se puede establecer un cierre automático independiente. Al vencer, Guardian termina el árbol de procesos de esa regla, revoca su autorización temporal y vuelve a exigir la contraseña. Tanto el periodo de confianza como el cierre automático admiten 5, 15 o 60 minutos y un valor personalizado de 1 a 10.080 minutos.

Cuando el cierre automático está activo, la interfaz deshabilita los periodos de confianza que lo superarían y limita el valor personalizado al mismo máximo. Guardian normaliza además cualquier política externa o antigua para que la confianza nunca dure más que la sesión antes de cerrarse.

Cada regla admite también un horario semanal con días, hora inicial y hora final. Puede protegerse solo dentro del intervalo y permitir la ejecución fuera de él, o bloquearse completamente fuera del horario permitido. Los intervalos que terminan antes de empezar continúan durante la madrugada del día siguiente; si ambas horas coinciden, el día seleccionado queda cubierto durante 24 horas. Guardian evalúa estas transiciones aunque la aplicación ya esté abierta: al entrar en un periodo protegido revoca las ejecuciones permitidas por horario y, en el modo restrictivo, las termina al salir del intervalo.

La sección **Diagnóstico** comprueba el servicio Guardian, la versión del motor, el canal IPC, Gate, la política cifrada, la tarea SYSTEM, el inicio silencioso y la integridad de las reglas. Los recursos protegidos se inspeccionan dentro del propio servicio SYSTEM, evitando falsos errores por las ACL que impiden leerlos desde una cuenta normal. La tarea se valida mediante la API nativa del Programador de tareas —estado, ejecutable y argumentos—, sin interpretar la salida localizada de `schtasks`. Si Guardian confirma un fallo reparable, **Reparar protección** solicita la contraseña maestra y elevación UAC, reinstala las capas del motor conservando reglas y credenciales y repite todas las comprobaciones.

Guardian conserva una línea base de integridad autorizada y copias de recuperación de sus binarios. Solo la instalación o actualización autorizada puede crear o sustituir esa línea base, y exige una identidad de firma consistente; una ausencia o error de identidad se trata como fallo, no como una configuración opcional. Las operaciones de instalación, reparación y actualización conservan primero una transacción recuperable y solo reemplazan la línea base al finalizar correctamente. La tarea SYSTEM se considera sana únicamente si coincide por completo con la ruta instalada, argumentos exactos, cuenta SYSTEM, nivel de ejecución, acciones, disparadores y recuperación esperados. El diagnóstico IPC que puede reparar exige un agente interactivo autenticado y está limitado para evitar abusos locales.

La sección **Actividad** conserva hasta 500 eventos dentro del estado local cifrado, por lo que el historial permanece después de reiniciar ProtectedApp. Los eventos se clasifican como bloqueos, accesos, avisos, errores o cambios del sistema y pueden buscarse y filtrarse. Cada rechazo real de la contraseña maestra o de una aplicación protegida se registra como aviso, sin guardar nunca el valor introducido. Un resumen compacto muestra los eventos, bloqueos, contraseñas fallidas y avisos o errores de las últimas 24 horas. El historial se puede limpiar desde la propia sección mediante un diálogo de confirmación; tampoco almacena argumentos ni contenido de scripts. El botón **Exportar** guarda exactamente los eventos visibles —respetando búsqueda y categoría— como CSV UTF-8 compatible con Excel o como JSON estructurado.

En **Configuración → Copia de seguridad** se pueden exportar y restaurar reglas, contraseñas propias, preferencias e historial mediante archivos `.pabackup`. La operación exige primero la contraseña maestra del equipo y cada archivo se protege además con una contraseña independiente de al menos ocho caracteres. El formato usa PBKDF2-SHA256 y AES-256-GCM, por lo que tanto una contraseña incorrecta como cualquier modificación del archivo impiden descifrarlo. La contraseña maestra y sus derivados no se exportan: al restaurar se conserva siempre la credencial maestra vigente en el equipo. Antes de reemplazar la configuración se valida por completo la copia, se muestra un resumen y se excluyen reglas dirigidas contra componentes de ProtectedApp o procesos esenciales de Windows.

En **Configuración → Actualización manual** se puede seleccionar un `ProtectedApp-Setup-x64.exe` obtenido por cualquier medio. Antes de ofrecer su instalación, ProtectedApp valida con Windows la firma Authenticode completa, exige exactamente el mismo certificado que firma la copia instalada, comprueba la identidad del producto y que la versión sea posterior y calcula el SHA-256 que se muestra en la confirmación. La operación vuelve a pedir la contraseña maestra, guarda y cierra todas las bóvedas abiertas y deja que Setup active el modo de mantenimiento antes de reemplazar el agente, Guardian y la tarea SYSTEM. Un archivo sin firma, alterado, firmado por otra identidad o con una versión igual o anterior se rechaza sin ejecutarse.

La pantalla de **Configuración** agrupa las opciones en pestañas: **General**, **Seguridad**, **Mantenimiento** e **Información**. Cada sección tiene desplazamiento propio para que los textos y controles no se solapen con escalado de Windows elevado.

En los formularios y cuadros de contraseña, `Intro` ejecuta la acción principal disponible —por ejemplo Añadir, Guardar, Continuar, Descifrar o Aceptar— y conserva las mismas validaciones que al pulsar el botón. Los campos destinados únicamente a buscar o filtrar no confirman acciones. Al cerrar un selector de archivos, ProtectedApp recupera explícitamente el foco, tanto si se eligió un archivo como si se canceló la operación.

Los cuadros de contraseña se restauran y reciben el foco explícitamente al abrirse desde la bandeja, desde un segundo acceso directo o desde una interceptación de Guardian. Si ya existe un cuadro esperando, una nueva solicitud de apertura lo trae al frente en vez de crear otro. La activación enlaza temporalmente el hilo de entrada con la ventana que tenía el foco y coloca el cursor en el campo de contraseña.

Los scripts se protegen por su ruta completa, no bloqueando globalmente el intérprete. Guardian inspecciona la línea de comandos de `cmd.exe`, `py.exe`, `python.exe`, `pythonw.exe`, PowerShell o `pwsh` y solo intercepta el proceso cuando contiene el `.bat` o `.py` configurado. Tras validar la contraseña, `.bat` se relanza con `cmd.exe`; para `.py`, Guardian conserva y reutiliza exactamente el intérprete, los argumentos y el directorio de trabajo del intento interceptado, incluidos entornos embebidos y virtuales.

## Ejecutar

```powershell
dotnet restore
dotnet run -c Release -p:Platform=x64
```

Requiere Windows 10 1809 o posterior y .NET 8 SDK. La primera ejecución solicita crear una contraseña maestra.

## Compilar y probar la solución

Para compilar todos los proyectos y sus pruebas desde un clon nuevo:

```powershell
dotnet restore
dotnet build ProtectedApp.sln -p:Platform=x64
dotnet test ProtectedApp.Guardian.Tests\ProtectedApp.Guardian.Tests.csproj -p:Platform=x64
dotnet test ProtectedApp.Vault.Tests\ProtectedApp.Vault.Tests.csproj -p:Platform=x64
```

La compilación de `ProtectedApp.csproj` individual ya selecciona `win-x64` por defecto. La referencia de las pruebas de bóveda conserva las mismas propiedades globales que la solución, por lo que MSBuild reutiliza una única compilación de la aplicación WinUI y puede ejecutar la solución en paralelo sin competir por sus archivos intermedios XAML.

## Crear el instalador

Ejecuta desde PowerShell:

```powershell
.\Build-Installer.ps1 -AllowUnsignedDevelopmentBuild
```

El script asigna automáticamente una versión nueva a cada compilación local. No
reutilices una versión de instalador: el Explorador puede conservar una DLL de
extensión versionada y rechazar una sustitución que, de otro modo, sería válida.
El script limpia primero cualquier publicación anterior, publica la aplicación
y el servicio como x64 autocontenido y genera
`artifacts\installer\ProtectedApp-Setup-x64.exe`. Antes de crear el Setup valida
los XBF/PRI requeridos y ejecuta una prueba real de inicialización WinUI sobre
la publicación final, evitando distribuir una mezcla de recursos antiguos y
nuevos. Si el compilador no está
disponible, descarga Inno Setup 6.7.3 desde su distribución oficial, verifica
su firma digital y lo prepara en `.tools`.

Setup instala por máquina en `Program Files`, crea el acceso del menú Inicio y
puede crear uno en el escritorio. En una actualización detiene temporalmente
Guardian, reemplaza los binarios y vuelve a iniciar el servicio conservando la
política cifrada. La opción final **Iniciar la protección en segundo plano** usa
el modo silencioso: no abre el panel ni solicita la contraseña maestra después
de instalar o actualizar.

La actualización entra en mantenimiento antes de cerrar cualquier proceso. El
instalador no usa el cierre forzado automático de Restart Manager, elimina la
observación anterior del agente mientras la marca autorizada sigue activa y no
abandona ese estado hasta que Guardian está iniciado y el nuevo agente vuelve a
estar presente. De este modo, una instalación autorizada no se interpreta como
manipulación y no activa el bloqueo de la sesión de Windows.

La entrada de Windows **Aplicaciones instaladas** y el botón **Desinstalar** de
ProtectedApp usan el mismo desinstalador. Antes de retirar el servicio, la tarea
SYSTEM y los archivos de la aplicación, se muestra solamente el cuadro de contraseña
maestra. Los archivos `.pavault` del usuario no se eliminan. Una copia recién instalada
que todavía no tenga contraseña también se puede desinstalar.

## Firma digital

El proceso de publicación puede firmar con SHA-256 y sello de tiempo RFC 3161 los ejecutables y ensamblados propios de ProtectedApp, Guardian y Gate antes de empaquetarlos, y después firma el instalador. Cada archivo se valida mediante la política Authenticode de Windows; una firma ausente, no confiable, perteneciente a otro certificado o sin sello de tiempo detiene la compilación.

Para un certificado de firma instalado en el almacén personal del usuario:

```powershell
$installerVersion = '1.4.186' # Elige una versión nueva que no se haya publicado
.\Build-Installer.ps1 -InstallerVersion $installerVersion `
  -SigningCertificateThumbprint HUELLASHA1 `
  -SigningCertificateStoreLocation CurrentUser `
  -RequireSignature
```

También se admite Microsoft Artifact Signing indicando `PROTECTEDAPP_ARTIFACT_SIGNING_DLIB` y `PROTECTEDAPP_ARTIFACT_SIGNING_METADATA`. Las credenciales o contraseñas PFX no se guardan en el repositorio ni se pasan en la línea de comandos. Para pruebas locales, `Signing\New-DevelopmentCodeSigningCertificate.ps1 -TrustForCurrentUser` crea un certificado autofirmado explícitamente marcado como desarrollo; la compilación exige además `-AllowDevelopmentCertificate` y sus binarios no deben distribuirse. Si no se configura ninguna identidad, el script falla de forma segura salvo que se indique expresamente `-AllowUnsignedDevelopmentBuild`. Las publicaciones oficiales deben usar siempre `-RequireSignature` y un firmante de confianza pública.

## Servicio de protección

La publicación incluye `Service\ProtectedApp.Guardian.exe`. En la primera ejecución interactiva, después de crear o validar la contraseña maestra, ProtectedApp solicita automáticamente la elevación UAC e instala el servicio. Desde **Configuración → Servicio de protección** se puede consultar su estado o retirarlo. La instalación debe iniciarse desde la interfaz, porque esta genera una autorización efímera para registrar la primera política sin aceptar configuraciones de otros procesos.

El servicio usa inicio automático, recuperación inmediata ante fallos y lanza el agente WinUI en la sesión interactiva del usuario. Si el agente permanece estable, un cierre posterior se recupera inmediatamente; si falla repetidamente durante su propia inicialización, Guardian aumenta los reintentos a 3, 8, 20 y 60 segundos para evitar bucles de procesos y volcados, manteniendo Gate cerrado durante toda la incidencia. La instalación también crea la tarea **ProtectedApp Guardian Health Check**, ejecutada como `SYSTEM`. Su supervisor permanece activo y comprueba el servicio cada segundo. Si se detiene, primero rearma desde la política cifrada todas las puertas preventivas que pudieran estar temporalmente abiertas, registra el incidente, ordena bloquear la sesión y reinicia el servicio. El propio Guardian vuelve a rearmarlas como primera operación al arrancar y elimina cualquier proceso protegido que hubiera comenzado durante la recuperación.

Después de haber observado el agente gráfico en ejecución, Guardian conserva esa evidencia durante el arranque actual. Por ello puede considerar su desaparición un cierre inesperado o forzado incluso si el servicio fue terminado al mismo tiempo, registrar el incidente y relanzarlo. También registra la detención o deshabilitación del servicio y la eliminación, desactivación o modificación de la tarea SYSTEM. Las señales se mantienen en una cola persistente de hasta 50 incidentes, sin caducar antes de que la interfaz pueda incorporarlas al historial cifrado. La interfaz procesa la cola pendiente como un único lote: agrupa motivos repetidos en el historial, guarda una vez, confirma hasta el último incidente y ejecuta como máximo un solo bloqueo de Windows. Si es terminada antes de confirmar el lote, vuelve a procesarlo al recuperarse. Windows no siempre permite identificar qué proceso produjo el cierre, por lo que el evento no atribuye sin pruebas la acción al Administrador de tareas.

Al reiniciarse Guardian se invalidan deliberadamente los tokens administrativos que solo existían en memoria. Si una modificación local coincide con ese reinicio, ProtectedApp la conserva cifrada y la marca como **sincronización pendiente** en vez de descartarla o mostrar un error genérico. Si el panel continúa desbloqueado espera a que el servicio vuelva y solicita una única reconexión; si el incidente bloqueó la sesión, sincroniza automáticamente la política tras el siguiente desbloqueo correcto. Cancelar la reconexión no provoca una sucesión de diálogos.

El disparo periódico de un minuto se conserva como segunda capa: si también terminan el supervisor `SYSTEM`, el Programador de tareas vuelve a iniciarlo. Ante una terminación del proceso del servicio, el Administrador de control de servicios solicita el primer reinicio sin demora y los siguientes a 1 y 5 segundos.

Si el servicio llegara a desaparecer fuera de una desinstalación autorizada, el supervisor no intenta reconstruir silenciosamente una instalación incompleta, pero mantiene las puertas rearmadas, termina las ejecuciones protegidas que detecte, registra el incidente y bloquea la sesión. La reparación posterior se realiza desde **Diagnóstico** con contraseña maestra y UAC.

Si el tipo de inicio del servicio cambia a **Manual** o **Deshabilitado**, el supervisor `SYSTEM` lo restaura a **Automático** antes de intentar iniciarlo. El propio servicio realiza además una comprobación secundaria de su configuración cada 10 segundos. Estos cambios también se consideran manipulación y se registran.

Las operaciones autorizadas de instalación, actualización y desinstalación crean una marca de mantenimiento para evitar alertas o bloqueos falsos. Debe conservarse toda la carpeta publicada en una ruta estable mientras el servicio esté instalado.

### Webhook opcional de manipulación

En **Configuración → Servicio de protección → Alerta remota de manipulación** se puede indicar cualquier endpoint HTTPS que acepte una petición `POST` con JSON. No depende de un proveedor concreto. Guardian solo genera alertas ante una manipulación real o cuando la recuperación automática falla y deja la protección en riesgo; los errores rutinarios de instalación, actualización y mantenimiento autorizado no se envían.

El contenido se limita a un identificador aleatorio del evento, un `installationId` aleatorio y estable, la fecha UTC y uno de los códigos cerrados `TamperDetected` o `RecoveryFailed`. El identificador de instalación permite distinguir equipos que compartan endpoint sin revelar el nombre del equipo, usuario, SID o dirección IP; se conserva al desactivar el webhook y solo cambia al eliminar por completo el estado de Guardian. No se incluyen rutas, nombres de archivos, contraseñas ni el motivo detallado del registro local. Si se activa la firma HMAC, el receptor debe compartir el secreto indicado en la configuración y validar la cabecera `X-ProtectedApp-Signature: sha256=<hex>`, calculada sobre los bytes UTF-8 exactos del cuerpo, incluido el `installationId`.

La URL y el secreto se cifran con DPAPI de máquina dentro de la carpeta privada de Guardian. Las alertas se guardan en una cola local limitada a 20 entradas y se envían fuera del flujo principal, con un timeout de 10 segundos y reintentos a los 5, 15, 30 y 60 segundos. Un fallo de red nunca impide el registro local, el bloqueo ni el rearmado de la protección. Al desactivar el webhook se eliminan su configuración y los eventos pendientes.

El webhook es una alerta informativa, no una frontera de seguridad: un administrador local puede detener Guardian, bloquear la red o eliminar su estado. Conviene que el endpoint no incluya secretos en la URL, conserve sus propios registros y responda con un código HTTP `2xx` solo después de aceptar el evento.

## Motor de aplicación de reglas

Guardian conserva una copia autoritativa y cifrada de las reglas bajo `%ProgramData%\ProtectedApp\Policy`. El ejecutable operativo del servicio se copia a `%ProgramData%\ProtectedApp\Service`; ambas carpetas conceden escritura únicamente a `SYSTEM` y administradores. La interfaz se comunica con Guardian mediante una named pipe local con ACL, comprueba el SID y la sesión del cliente y vincula los tokens administrativos al PID que se autenticó.

Para los archivos `.exe`, Guardian instala una puerta de ejecución por ruta completa mediante Image File Execution Options (IFEO). Windows inicia `ProtectedApp.Gate` en lugar de cargar la imagen protegida; Gate registra el intento en Guardian y termina sin ejecutar la aplicación. La misma puerta se instala sobre los lanzadores Python detectados en el perfil y sobre intérpretes embebidos encontrados cerca de cada `.py` protegido (por ejemplo `python_embeded`, `venv` o `.venv`). Gate inspecciona los argumentos antes de iniciar `py.exe`/`python.exe`, bloquea únicamente el script protegido y reenvía normalmente el resto. Guardian aprende además cualquier intérprete Python nuevo que alcance la capa de respaldo, de modo que su siguiente ejecución pase por Gate. Después de validar la contraseña, Guardian mantiene abierta la familia del intérprete mientras existan procesos de esa ejecución, para que workers y subprocesos conserven su padre, consola y manejadores originales; al finalizar el último proceso restaura automáticamente las puertas. Durante ese intervalo, la suscripción inmediata a `Win32_ProcessStartTrace` continúa comprobando otros intentos protegidos. Si el servicio no responde, Gate falla de forma cerrada y el destino continúa sin iniciarse.

El servicio mantiene además la suscripción como `SYSTEM` a los eventos de creación de procesos (`Win32_ProcessStartTrace`). Esta segunda capa suspende inmediatamente cualquier candidato que no haya pasado por Gate, confirma su ruta o línea de comandos mientras permanece congelado y lo termina si no está autorizado. También protege `.bat` y lanzadores Python no detectados; el barrido configurable de 500–1000 ms se conserva como último respaldo.

Cada nueva instancia no autorizada se termina aunque ya haya una petición de contraseña pendiente; solo se muestra un único cuadro. Una contraseña válida crea una autorización temporal para ese ejecutable en la sesión actual y mantiene retirada su puerta IFEO mientras al menos uno de sus procesos continúe vivo. Así, navegadores Chromium pueden crear directamente procesos auxiliares conservando sus canales y manejadores heredados. Al cerrarse el último proceso, Guardian restaura inmediatamente Gate. Si Guardian se reinicia, pierde todas las autorizaciones y realiza un nuevo barrido, por lo que las aplicaciones que hubieran quedado abiertas se cierran de forma segura.

Los intentos de contraseña tienen protección progresiva e independiente. La contraseña maestra y cada regla mantienen su propio contador, por lo que los fallos en una aplicación no bloquean las demás ni el panel de gestión. Los dos primeros rechazos permiten reintentar; el tercero inicia una espera de 5 segundos y los siguientes aumentan a 15, 30, 60 y un máximo de 300 segundos. La ventana muestra los intentos y una cuenta atrás, mantiene disponibles **Cancelar** y `Escape`, y se reactiva automáticamente al terminar. Guardian conserva los bloqueos en su carpeta privada aunque el servicio se reinicie y descarta el historial tras 15 minutos sin nuevos fallos o inmediatamente después de una autenticación correcta. El inicio y fin de cada bloqueo quedan registrados sin almacenar la contraseña introducida.

Cuando Guardian está activo, tanto la contraseña maestra como las contraseñas por aplicación se verifican dentro del servicio, no contra la copia local. El modo local de respaldo aplica la misma progresión durante la ejecución actual. Si se pierde el estado local, la interfaz puede restaurar reglas y credenciales desde Guardian después de validar la contraseña maestra.

## Modelo de seguridad

- Contraseñas con PBKDF2-SHA256, sal aleatoria y 210.000 iteraciones.
- Estado local cifrado con DPAPI para el usuario actual en `%LOCALAPPDATA%\ProtectedApp\state.dat`; el antiguo `state.json` se migra y elimina automáticamente.
- Inicio automático opcional mediante `HKCU\...\Run`. Los inicios de Windows, Guardian y el supervisor usan modo silencioso: mantienen el agente en segundo plano sin mostrar la interfaz ni solicitar la contraseña. Solo una apertura manual, el menú de la bandeja o un intento de ejecutar una aplicación protegida muestran la ventana correspondiente.
- La ventana se oculta al cerrarse y la protección continúa en segundo plano. En **Configuración → Icono en la bandeja** se puede ocultar o volver a mostrar el icono; aunque esté oculto, la interfaz se recupera ejecutando ProtectedApp desde Inicio o desde su acceso directo.
- Un supervisor oculto relanza la aplicación tras una terminación inesperada y ProtectedApp vuelve a crear el supervisor si este se cierra por separado.
- Una tarea programada como `SYSTEM`, con comprobación continua cada 250 ms y relanzamiento periódico de respaldo, rearma las puertas y recupera el servicio después de una detención manual. Los eventos se escriben con el origen `ProtectedAppGuardian` y la cola persistente se guarda en `%ProgramData%\ProtectedApp`.
- Ante manipulación se invalidan accesos temporales, se reactiva el monitor y se bloquea la sesión; nunca se apaga ni se cuelga deliberadamente el sistema.

La aplicación de reglas se ejecuta como `SYSTEM` cuando Guardian está instalado. El modo local queda como respaldo para instalaciones sin servicio. Esto protege frente a usuarios estándar y cierres accidentales, pero no constituye una frontera frente a un administrador local: un administrador puede retirar simultáneamente el servicio, la tarea y los archivos. Para equipos administrados conviene complementar ProtectedApp con BitLocker, Secure Boot y políticas App Control for Business/WDAC probadas primero en modo auditoría.

## Hardening combinado de Windows

ProtectedApp protege el uso de aplicaciones, carpetas y bóvedas dentro de Windows; no sustituye la seguridad del arranque, el cifrado de disco ni el control de software del sistema. En particular, un administrador local o código que ya se ejecute como `SYSTEM` está fuera de su frontera de confianza. Para equipos personales sensibles o administrados, se recomienda aplicar las capas siguientes de forma gradual.

1. **BitLocker.** Activa el cifrado de unidad del sistema y guarda la clave de recuperación fuera del equipo (por ejemplo, en una cuenta o repositorio corporativo autorizado). Un TPM protege las claves frente a cambios de arranque; añadir un PIN de prearranque aumenta la protección ante robo físico, a cambio de requerir interacción en cada inicio. Comprueba que puedes recuperar el equipo antes de depender de esta capa.
2. **Secure Boot.** Mantén UEFI y Secure Boot activados y actualizados. Evita cambiar claves de plataforma o desactivar Secure Boot como solución a problemas de controladores sin disponer de un procedimiento de recuperación. Esta capa ayuda a que el arranque use componentes firmados y además refuerza las políticas App Control firmadas.
3. **App Control for Business (WDAC).** Usa WDAC para permitir solo software de confianza. Empieza siempre en **modo auditoría**: instala y usa normalmente las aplicaciones, controladores, scripts y actualizaciones previstos, y revisa los eventos antes de bloquear nada. Windows registra los binarios que una política habría bloqueado en `Aplicaciones y servicios → Microsoft → Windows → CodeIntegrity → Operational`; los eventos de MSI y scripts se revisan en `AppLocker → MSI and Script`.
4. **Paso a aplicación.** Tras revisar los eventos y añadir las reglas necesarias, despliega primero en un equipo de prueba o un grupo pequeño. Conserva una cuenta administradora de recuperación, la clave de BitLocker y un medio de recuperación. Para la primera política WDAC en modo aplicado, valora las opciones de recuperación de arranque/auditoría indicadas por Microsoft antes de extenderla al resto de equipos.

No actives una política WDAC restrictiva directamente en modo aplicado ni copies una política de otro equipo sin probarla: puede bloquear software legítimo, actualizaciones o controladores. ProtectedApp debe incluirse en la lista de software permitido, junto con Guardian, Gate, el instalador firmado y los componentes WinUI/Dokany que necesite.

Referencias oficiales: [despliegue de App Control for Business](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/deployment/appcontrol-deployment-guide), [uso de eventos de auditoría](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/deployment/audit-appcontrol-policies) y [paso a modo aplicado](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/deployment/enforce-appcontrol-policies).

## Distribución

La aplicación se distribuye mediante `ProtectedApp-Setup-x64.exe`. El proyecto puede usar un certificado de desarrollo privado para pruebas y equipos de confianza; ese certificado debe instalarse previamente en los equipos destinatarios. Una identidad de firma pública para SmartScreen o publicación comercial no forma parte de este proyecto.

### Distribución y licencias

ProtectedApp es software libre bajo [GPL-3.0-or-later](LICENSE). Puedes usarlo,
estudiarlo, modificarlo y redistribuirlo conforme a esa licencia. Las versiones
modificadas que distribuyas deben conservar los avisos de copyright y ofrecer su
código fuente bajo los mismos términos. La marca ProtectedApp no concede por sí
misma autorización para presentar una versión modificada como distribución
oficial.

El instalador incluye los [avisos de terceros](Legal/THIRD-PARTY-NOTICES.txt).
Durante cada compilación oficial, `Build-Installer.ps1` recopila los textos
exactos de licencia y avisos de todos los paquetes NuGet restaurados, incorpora
LGPLv3, GPLv3 y MIT para Dokany/DokanNet y cancela la entrega si detecta términos
de Windows App SDK identificados como `Engineering Preview`.

Antes de publicar, configura un canal de soporte y los avisos privados de seguridad de GitHub. Cada lanzamiento debe ofrecer el código fuente correspondiente y los scripts de compilación junto a sus binarios. Los servicios en línea que se añadan necesitan documentación de privacidad propia.

La [lista de preparación de distribución](Legal/COMMERCIALIZATION-CHECKLIST.md) recoge además privacidad, firma pública, soporte de seguridad, ausencia actual de activación técnica y preparación para el Reglamento de Ciberresiliencia de la UE.

ProtectedApp usa `DokanNet` bajo MIT y distribuye el runtime oficial Dokany 2.3.1.1000 bajo LGPLv3/MIT como componente separado y sustituible. No se distribuyen modificaciones de Dokany. El código fuente oficial correspondiente queda enlazado por versión en los avisos instalados.

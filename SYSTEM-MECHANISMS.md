# Mecanismos del sistema y por qué existen

> [English](SYSTEM-MECHANISMS.en.md) | **Español**

ProtectedApp usa varios mecanismos de Windows que el software de seguridad y las
personas que revisan código miran, con razón, con desconfianza: un valor
`Debugger` de Image File Execution Options (IFEO), un servicio de Windows, una
tarea programada de `SYSTEM`, la finalización de procesos y cambios de permisos
en carpetas. Cada uno es necesario para lo que promete el producto. Esta página
indica, para cada mecanismo, qué hace, por qué una alternativa más simple no
bastaba, hasta dónde está limitado, dónde está el código y cómo verlo y
eliminarlo.

Todo lo aquí descrito se puede comprobar en el código fuente. Cuando una
afirmación trata de la ausencia de un comportamiento, se indica la búsqueda que
la respalda.

## Resumen

| Mecanismo | Para qué | Límite de alcance | Se elimina con |
| --- | --- | --- | --- |
| Puerta IFEO `Debugger` | Pedir contraseña *antes* de que se ejecute un programa protegido | Solo las rutas exactas que el usuario activó; falla cerrado | Quitar la regla o desinstalar |
| Servicio `ProtectedAppGuardian` | Aplicar las reglas con la ventana cerrada | Solo local; actúa sobre las reglas configuradas | Desinstalar |
| Tarea `SYSTEM` de comprobación | Rearmar las puertas si el servicio se detiene | Ejecuta un único ejecutable incluido | Desinstalar |
| Finalización de procesos | Bloquear programas protegidos cuando corresponde | Solo procesos que coinciden con una regla activa | No aplica (comportamiento) |
| Denegación de ACL en carpetas | Bloquear una carpeta protegida | Solo carpetas elegidas por el usuario | Quitar la regla o desinstalar |
| Controlador Dokany | Unidades editables de bóvedas cifradas | De terceros; solo se usa al montar | Se conserva si es compartido; se quita aparte |
| PowerShell oculto en el instalador | Registrar y retirar los componentes anteriores | Scripts incluidos, desde la carpeta de instalación | No aplica |

## 1. Puerta IFEO `Debugger`

**Qué hace.** Por cada `.exe` protegido y activo, el servicio Guardian escribe
una subclave en
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<imagen>\`.
Cuando Windows inicia ese programa, inicia `ProtectedApp.Gate.exe` en su lugar.
La puerta comunica el intento bloqueado a Guardian por una tubería local y
termina; después el agente de ProtectedApp pide la contraseña. **La puerta nunca
inicia el programa original.** Solo lo hace Guardian, cuando se acepta la
contraseña, y lo inicia en la sesión del propio usuario con el token de ese
usuario (véase la sección 4).

**Por qué no algo más simple.** Windows no ofrece un enlace de modo usuario
soportado que signifique «pregunta antes de ejecutar este programa». Vigilar los
procesos nuevos y cerrarlos deja que el programa se ejecute, y quizá lea o
modifique datos, antes de detenerlo. IFEO es un mecanismo antiguo de Windows que
intercepta el propio inicio. También lo usan herramientas legítimas, pero quien
analiza malware reconoce la técnica, así que sus límites importan:

- **Filtrada por ruta, no por nombre.** Cada regla activa `UseFilter=1` en la
  imagen y crea una subclave con `FilterFullPath` igual a una ruta completa. Otro
  programa con el mismo nombre de archivo en otra carpeta no se intercepta
  ([ExecutionGateManager.cs](ProtectedApp.Service/ExecutionGateManager.cs)).
- **Marcada y reversible.** Cada subclave lleva `ProtectedAppManaged=1`. La
  limpieza elimina solo las entradas marcadas. Si `UseFilter` ya tenía un valor,
  se guarda como `ProtectedAppPreviousUseFilter` y se restaura después.
- **Autorización temporal.** Tras una contraseña correcta se quita el valor
  `Debugger` para que el inicio prosiga, y se restaura cuando el programa deja de
  ejecutarse (nunca antes de unos 8 segundos desde la autorización).
- **Falla cerrado.** Si Guardian no responde en 2 segundos, la puerta muestra un
  aviso y el programa no se inicia
  ([ProtectedApp.Gate/Program.cs](ProtectedApp.Gate/Program.cs)).
- **Nada más.** La puerta solo se conecta a la tubería local de Guardian. No usa
  la red ni actúa sobre otros procesos.

**Scripts de Python.** Un script `.py` no es un ejecutable, así que los
intérpretes y lanzadores (`py.exe`, `pyw.exe`, `python.exe`, `pythonw.exe`)
encontrados en las ubicaciones estándar y cerca del script se protegen con la
misma puerta, y la línea de comandos interceptada se envía a Guardian.

*Efecto sobre otros scripts.* Mientras haya al menos una regla `.py` activa,
**cada** inicio de esos intérpretes se intercepta, también el de scripts ajenos a
ProtectedApp. Cuando la línea de comandos no hace referencia a un script
protegido, Guardian levanta esa entrada de puerta para el intérprete y lo vuelve
a iniciar con los argumentos y el directorio de trabajo originales, de modo que
el script se ejecuta con normalidad
([GuardianEnforcer.cs](ProtectedApp.Service/GuardianEnforcer.cs)). Como Guardian
hace ese segundo inicio en la sesión del usuario, el proceso nuevo no es hijo de
quien lo invocó, parte de una copia nueva del entorno del usuario, no hereda la
consola ni la entrada y salida redirigidas del llamante, y su código de salida no
se devuelve a quien lo invocó. Un script que dependa de alguna de estas cosas
(salida encadenada en un terminal o un entorno virtual activado en la consola que
lo llama) puede comportarse de forma distinta. Guardian reinicia el intérprete
solo cuando quien lo llama es el usuario de la sesión interactiva; un inicio desde
un servicio, otra cuenta o `runas` se bloquea con un aviso, porque reiniciarlo
ejecutaría el comando de quien llama con otra identidad. Desactivar o quitar las
reglas `.py` termina la interceptación.

*Qué cuenta como ejecutar un script protegido.* Un script se reconoce por su ruta
completa o por un nombre relativo que se resuelve contra el directorio desde el
que se lanzó el proceso (`python backup.py`, `.\backup.py`), también dentro de una
cadena de shell como `cmd /c "python backup.py"`. La línea de comandos se divide
con el analizador propio de Windows (`CommandLineToArgvW`), así que las comillas y
las barras invertidas de escape no pueden ocultar el script
([WindowsCommandLine.cs](Shared/WindowsCommandLine.cs)). Las rutas cortas 8.3
existentes en discos locales se normalizan a su ruta larga antes de compararlas;
una ruta de red nunca se consulta, porque el servicio SYSTEM contactaría con el
servidor que indique quien llama, y se compara tal como está. No se reconoce si
se inicia como `python -m módulo` o desde una cadena de shell que antes cambie de
directorio: resolver esos casos exigiría interpretar módulos de Python o el
lenguaje de `cmd`/PowerShell y podría identificar un archivo distinto. Tras la aprobación, Guardian vuelve a iniciar solo el script, los argumentos que lo
siguen y las opciones inocuas del intérprete (`-u`, `-O`, `-X`, `-W`, selectores
de versión de `py` como `-3.11`), en el directorio de trabajo original. Las
opciones que ejecutan otro código (`-c`, `-m`, `-i`) y las envolturas de shell se
descartan, de modo que aprobar un script nunca autoriza una carga distinta
([ProtectedTarget.cs](Shared/ProtectedTarget.cs)).

**Directory Opus.** Si Directory Opus está instalado, Guardian elimina las
marcas de diagnóstico Page Heap de `Image File Execution Options\dopus.exe`.
Page Heap es una opción de depuración de Windows, no una protección, y con ella
activada Opus se cierra por paradas del verificador al explorar unidades Dokany.
Solo se quitan el bit de Page Heap y `PageHeapFlags`; no se añade nada
([DirectoryOpusCompatibility.cs](ProtectedApp.Service/DirectoryOpusCompatibility.cs)).

## 2. Servicio Guardian y tarea `SYSTEM` de comprobación

**Servicio.** `ProtectedAppGuardian` arranca automáticamente para que las reglas
se apliquen antes de que nadie abra la ventana y para que un proceso normal de
usuario no pueda terminar la protección cerrando la interfaz. Lo instala
[Install-Service.ps1](ProtectedApp.Service/Install-Service.ps1) con reinicio
automático ante fallos.

**Tarea de comprobación.** `ProtectedApp Guardian Health Check` se ejecuta como
`SYSTEM` al arrancar y cada minuto, y ejecuta un solo comando: el
`ProtectedApp.Guardian.exe --health-watch` instalado. Comprueba la integridad
del binario de Guardian y de su configuración, rearma las puertas IFEO y reinicia
el servicio si se detuvo o fue manipulado. Si encuentra Guardian detenido o
irrecuperable, puede bloquear la sesión de Windows, **como máximo una vez por
arranque** ([GuardianHealthCheck.cs](ProtectedApp.Service/GuardianHealthCheck.cs)).

**Por qué.** Sin ella, detener el servicio eliminaría la protección en silencio.

**Protección de sus propios archivos.** `%ProgramData%\ProtectedApp` solo pueden
modificarlo `SYSTEM` y los administradores; los usuarios estándar pueden leer y
ejecutar. La instalación registra el SHA-256 y el firmante de la aplicación de
gestión, y Guardian solo acepta solicitudes privilegiadas de esa identidad.

**Límite.** No es una frontera frente a un administrador local ni a `SYSTEM`. Un
administrador puede detener el servicio o editar el registro. Así lo indica el
[modelo de seguridad](SECURITY-MODEL.md), y la tarea existe para hacer visible la
manipulación silenciosa y restaurar el estado configurado, no para vencer a un
administrador.

## 3. Tubería local

Guardian escucha en una tubería con nombre local. Su lista de acceso concede
control total a `SYSTEM` y a la identidad del servicio, concede lectura y
escritura a Usuarios autenticados y **deniega expresamente el SID de red**, de
modo que no es accesible desde otros equipos. La autorización no depende solo de
esa lista: las sesiones reciben tokens ligados al SID del usuario, al identificador
del proceso que llama y a su hora de inicio, y caducan. Los clientes se conectan con el nivel de suplantación *Identification*:
el servicio puede leer quién llama, pero no puede actuar como ese usuario, aunque
otro proceso respondiera en la tubería en su lugar
([GuardianProtocol.cs](Shared/GuardianProtocol.cs)). Las operaciones de gestión
(cambiar la política, bloquearlo todo, configurar el webhook) exigen además que
quien llama sea el ejecutable de ProtectedApp registrado
([GuardianIpcServer.cs](ProtectedApp.Service/GuardianIpcServer.cs)). Los intentos
fallidos de contraseña se limitan.

## 4. Iniciar un programa en la sesión del usuario

Un servicio se ejecuta en la sesión 0 y no puede mostrar un programa en el
escritorio del usuario. Para iniciar un programa autorizado, Guardian obtiene el
token del usuario activo (`WTSQueryUserToken`), lo duplica y llama a
`CreateProcessAsUser` en el escritorio interactivo. Por tanto, el programa se
ejecuta con **los privilegios del propio usuario, no como `SYSTEM` ni elevado**
([InteractiveProcessLauncher.cs](ProtectedApp.Service/InteractiveProcessLauncher.cs)).
El ejecutable es la ruta que el usuario configuró en la regla.

## 5. Finalización de procesos

Cuando se aplica una política de bloqueo, o cuando un programa protegido se
ejecuta sin autorización, Guardian lo termina. Un proceso solo es objetivo si la
ruta de su ejecutable coincide con una regla activa o, en una regla de script, si
es un intérprete conocido cuya línea de comandos hace referencia a ese script. En
un bloqueo inmediato, Guardian pide primero a las ventanas que se cierren con
normalidad y espera un periodo de gracia; fuerza la terminación solo de los que no
se cerraron. El producto avisa antes de las acciones de bloqueo porque puede
perderse trabajo sin guardar en un programa cerrado
([GuardianEnforcer.cs](ProtectedApp.Service/GuardianEnforcer.cs)). No termina
procesos que no coinciden con ninguna regla.

## 6. Bloqueo de carpetas

Las carpetas protegidas se bloquean añadiendo una entrada de denegación a su
lista de control de acceso. Antes de cambiarla, Guardian guarda la lista original
y la restaura al quitar la regla. Se rechaza una carpeta que ya contiene una
aplicación protegida o que está dentro o alrededor de otra carpeta protegida. Se
conserva la lectura de atributos para que el Explorador y Directory Opus resuelvan
los iconos. La desinstalación **ejecuta primero `--restore-folders` y se detiene si
falla**, de modo que una eliminación interrumpida no deja una carpeta bloqueada
([FolderProtectionService.cs](ProtectedApp.Service/FolderProtectionService.cs),
[Remove-Service.ps1](ProtectedApp.Service/Remove-Service.ps1)).

## 7. Unidades de bóveda (Dokany)

Las bóvedas editables aparecen como unidades virtuales mediante
[Dokany](https://dokan-dev.github.io/), un controlador de sistema de archivos de
código abierto y de terceros. El instalador incluye su MSI y lo instala en
silencio solo si falta un runtime compatible. ProtectedApp no contiene ningún
controlador de kernel: el repositorio no incluye binarios compilados y sus propios
componentes son de modo usuario. Como Dokany puede compartirse con otro software,
el desinstalador lo conserva.

## 8. Comportamiento del instalador

- El instalador y el desinstalador ejecutan scripts de PowerShell incluidos con
  `-NoProfile -NonInteractive -ExecutionPolicy Bypass`, ocultos, desde la carpeta
  de instalación. `-ExecutionPolicy Bypass` se aplica solo a esas invocaciones y no
  cambia la política del equipo. Los scripts están en el repositorio:
  [Install-Service.ps1](ProtectedApp.Service/Install-Service.ps1),
  [Remove-Service.ps1](ProtectedApp.Service/Remove-Service.ps1),
  [Cleanup-ShellExtensions.ps1](ProtectedApp.ShellExtension/Cleanup-ShellExtensions.ps1),
  [Remove-ShellExtension.ps1](ProtectedApp.ShellExtension/Remove-ShellExtension.ps1)
  e [Install-ShellExtension.ps1](ProtectedApp.ShellExtension/Install-ShellExtension.ps1).
  A pesar de su nombre, el último ya no instala nada: elimina registros de
  versiones anteriores. **Estos cuatro scripts de limpieza heredada solo se
  inician si se encuentran entradas de registro o archivos de la extensión
  antigua**, así que una instalación limpia y su eliminación no los ejecutan. Lo
  que siempre se ejecuta es [Prepare-Installation.ps1](Installer/Prepare-Installation.ps1)
  (cierra la aplicación y detiene el servicio antes de una actualización),
  [Update-Installed-Service.ps1](ProtectedApp.Service/Update-Installed-Service.ps1)
  (registra o actualiza el servicio) y `Remove-Service.ps1` al desinstalar.
- La integración con el Explorador es un conjunto de comandos estáticos del menú
  contextual bajo `HKLM\Software\Classes` (cifrar carpeta, abrir y montar
  `.pavault`, desmontar unidad), cada uno con `ProtectedApp.exe` y un argumento.
  **No se registra ninguna extensión de shell en proceso ni servidor COM**; el
  instalador excluye la DLL heredada.
- El inicio con Windows usa la clave `Run` del usuario actual y arranca en
  silencio.
- **Desinstalar exige la contraseña maestra** (`--authorize-uninstall`). Después
  restaura las carpetas, elimina la tarea, el servicio, las puertas IFEO, el
  origen de eventos y `%ProgramData%\ProtectedApp`, y borra
  `%LocalAppData%\ProtectedApp`.

## Lo que ProtectedApp no hace

Cada punto se puede volver a comprobar buscando en el código fuente:

- **Sin inyección de código.** No usa `WriteProcessMemory`, `CreateRemoteThread`,
  `VirtualAllocEx`, `QueueUserAPC` ni `SetWindowsHookEx`.
- **Sin registro de teclas ni hooks globales.** La única API de teclado es
  `RegisterHotKey`, para el atajo de bloqueo inmediato que el usuario define en
  Ajustes.
- **Sin persistencia oculta.** Los componentes persistentes son los de esta
  página.
- **Sin telemetría, código remoto ni actualizaciones automáticas.** El uso de red
  se limita al webhook opcional de alertas descrito en el [modelo de
  seguridad](SECURITY-MODEL.md). Las actualizaciones solo se instalan desde un
  archivo que el usuario elige.
- **Sin recopilación de credenciales.** Las contraseñas se comprueban en local
  (se guardan como hashes PBKDF2-SHA256) y nunca salen del equipo. La única
  función de red es el webhook opcional, cuyo contenido no incluye contraseñas ni
  rutas.

## Verlo y eliminarlo

Comprobaciones de solo lectura en un PowerShell con privilegios elevados:

```powershell
# Entradas IFEO creadas por ProtectedApp (revisa también la vista WOW6432Node)
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options' |
  ForEach-Object { Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue } |
  Where-Object { (Get-ItemProperty $_.PSPath).ProtectedAppManaged -eq 1 } |
  ForEach-Object { Get-ItemProperty $_.PSPath | Select-Object FilterFullPath, Debugger }

Get-Service ProtectedAppGuardian
schtasks /Query /TN "ProtectedApp Guardian Health Check" /V /FO LIST
```

Para eliminarlo todo, usa el desinstalador oficial desde la configuración de
Windows e introduce la contraseña maestra. No borres las entradas del registro a
mano mientras haya carpetas bloqueadas; el desinstalador restaura primero sus
permisos.

## Informar de un falso positivo o de una duda

Si un antivirus marca una compilación, abre un issue con el nombre del escáner,
el nombre de la detección y el SHA-256 del archivo, para compararlo con los hashes
publicados en la versión y notificarlo al fabricante. Para una cuestión de
seguridad, usa el proceso privado de [SECURITY.md](SECURITY.md).

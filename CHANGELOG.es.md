# Historial de cambios

Aquí se documentan los cambios relevantes de ProtectedApp. Las entradas se
agrupan por versión o candidato de revisión que las contiene. Un **candidato de
revisión sin firmar** no es una versión pública y no debe distribuirse como tal;
consulta [RELEASING.md](RELEASING.md) para conocer el proceso de publicación y
firma.

> [English](CHANGELOG.md)

## Pendiente de publicación

### Seguridad

- Si un corte de energía deja un temporal candidato de escritura PAVLT y falta
  el contenedor principal, ProtectedApp ofrece ahora una recuperación explícita
  al abrir la bóveda. Nunca elige entre varios temporales, no sobrescribe un
  principal que haya reaparecido, rechaza enlaces y candidatos malformados, y
  exige tanto la contraseña como la identidad cifrada de la bóveda antes de
  mover el temporal de forma atómica.
- Se añadió un corpus determinista de regresión para corrupción y recuperación
  de bóvedas PAVLT003/4: las cabeceras, índices cifrados, bloques de archivo,
  contenedores truncados, diarios delta y copias cifradas dañados deben
  rechazarse sin modificar la bóveda original. Las pruebas también dejan
  explícito el límite de verificación diferida: el sobre se autentica al abrir
  la bóveda y cada bloque de archivo antes de leerse.
  Una escritura virtual interrumpida también debe conservar el contenedor
  previo byte a byte y eliminar su archivo temporal incompleto.
  Las entradas virtuales con traversal o rutas absolutas deben rechazarse antes
  de que puedan sustituir una bóveda existente.
  Las rutas virtuales duplicadas sin distinguir mayúsculas también se rechazan
  antes de crear un contenedor temporal. Se acepta el límite exacto de 20.000
  entradas y se rechaza la siguiente antes de crear un temporal.

## 1.4.211 — 2026-09-22 (pre-release de desarrollo)

### Corregido

- Un script `.py` o `.bat` protegido que se iniciaba con un nombre relativo
  (`python backup.py`, `.\backup.py`) no se reconocía y se ejecutaba sin
  contraseña. Guardian, su supervisor independiente de recuperación de
  emergencia, y el monitor y el seguimiento de inactividad de la aplicación
  resuelven ahora los nombres relativos contra el directorio desde el que se
  lanzó el proceso, leído del propio proceso (también de 32 bits), y reconocen
  además el prefijo de ruta `\\?\`. La línea de comandos se divide con el
  analizador propio de Windows (`CommandLineToArgvW`), de modo que una comilla
  escapada con barra invertida como en `python -X\" backup.py` ya no puede
  ocultar el script, y se reconoce un script dentro de una cadena de shell
  (`cmd /c "python backup.py"`). Las rutas cortas 8.3 existentes en discos
  locales también se normalizan a su ruta larga de Windows antes de compararlas;
  una ruta de red nunca se consulta (el servicio SYSTEM contactaría con el
  servidor que indique quien llama), así que se compara tal como está. Iniciarlo como
  `python -m módulo` o desde una cadena de shell que antes cambie de directorio
  sigue sin reconocerse.
- Tras aprobar un script `.py` protegido, se descartaban sus argumentos y se
  iniciaba en la carpeta del script. Ahora conserva los argumentos que siguen al
  script, las opciones inocuas del intérprete (`-u`, `-X`, `-W`, `py -3.11`) y el
  directorio de trabajo original. Las opciones que ejecutan otro código (`-c`,
  `-m`) y las envolturas de shell siguen sin reproducirse nunca, de modo que
  aprobar un script no puede autorizar una carga distinta.
- El inicio de un intérprete interceptado para un usuario sin configuración de
  ProtectedApp ya no se rechaza por falta de política; ahora pasa como cualquier
  otro script.

### Seguridad

- El servicio SYSTEM ya no lee el sistema de archivos ni la red para una ruta
  que controla quien llama. Path.GetFullPath expande los alias 8.3 siempre que
  una ruta contiene «~», y File.Exists, Directory.Exists y el listado de un
  directorio son lecturas del sistema de archivos; con una ruta en un recurso
  compartido, cada una bloqueaba unos 21 s si el servidor no respondía, y se
  autenticaría contra él con la cuenta del equipo si respondía. Bastaba un
  proceso iniciado desde un recurso compartido, un Gate iniciado con un
  argumento manipulado o un directorio de trabajo en un recurso compartido. Las
  rutas de imagen de procesos, las comprobaciones de identidad de quien llama a
  la tubería, las rutas que envía el Gate y las comprobaciones de intérprete y
  directorio de trabajo normalizan ahora solo texto, y consultan el disco
  únicamente en una unidad local fija.
- Guardian ya no inicia un programa en la sesión interactiva en nombre de quien
  llama si este no es el usuario de esa sesión (un servicio, otra cuenta o un
  inicio con «ejecutar como»). Antes, el programa se iniciaba con el token del
  usuario de la sesión, ejecutando el comando de quien llamaba con otra
  identidad.

### Cambiado

- Los clientes de la tubería de Guardian (la puerta de protección y la
  aplicación de gestión) se conectan ahora con el nivel de suplantación
  Identification en lugar de Impersonation. Guardian solo necesita saber quién
  llama, de modo que puede seguir leyendo la identidad del llamante pero ya no
  puede actuar como ese usuario, aunque otro proceso responda en la tubería en
  lugar del servicio.
- El instalador y el desinstalador lanzan los cuatro scripts de limpieza de la
  extensión heredada del Explorador solo cuando quedan entradas de registro o
  archivos de esa extensión ya eliminada. Una instalación limpia, y su
  eliminación, ya no inician esos procesos ocultos de PowerShell.

### Documentación

- Se añade [SYSTEM-MECHANISMS.md](SYSTEM-MECHANISMS.md), que explica por qué es
  necesario cada mecanismo sensible de Windows (puerta IFEO, servicio, tarea
  `SYSTEM`, finalización de procesos, bloqueo de carpetas, Dokany, scripts del
  instalador), hasta dónde está limitado y cómo inspeccionarlo y eliminarlo,
  incluido el efecto que tiene un script de Python protegido sobre otros
  scripts ejecutados por el mismo intérprete.

## 1.4.210 — 2026-09-21 (pre-release de desarrollo)

### Corregido

- La persistencia de estado serializa ahora los guardados simultáneos, usa
  temporales únicos y los confirma antes del reemplazo atómico, evitando que
  escrituras concurrentes corrompan la configuración local cifrada.
- La versión fuente coincide ahora con la pre-release de desarrollo actual, de
  modo que una compilación local independiente no parte de una referencia
  obsoleta.
- El cliente IPC y las consultas de sesión de Guardian liberan ahora sus
  identificadores de proceso de inmediato, y las respuestas IPC malformadas se
  tratan como errores controlados de disponibilidad.
- Guardian rechaza de forma determinista JSON IPC malformado, y la comparación
  del secreto de instalación borra sus copias temporales de bytes tras usarlas.
- Los diagnósticos de arranque y de fallos (`launch.log`, `crash.log`,
  `launch-exception.log`) tienen ahora un tamaño estrictamente acotado:
  ningún archivo supera 256 KiB. Una entrada que no cabe rota antes el archivo
  a un único `.1`, y una sola entrada mayor que el límite se trunca con una
  marca, en lugar de crecer durante toda la vida de la instalación.
- Quince diálogos y etiquetas (avisos de archivo o bóveda duplicados,
  confirmación de eliminación permanente, mensajes del desinstalador y de las
  alertas remotas, entre otros) se mostraban en español con la interfaz en
  inglés. La confirmación de eliminación permanente se localiza ahora al
  construir el diálogo, desde el mismo código que valida la respuesta, y
  acepta siempre también la palabra original en español, de modo que a un
  usuario en inglés nunca se le pide una palabra que se rechaza.
- El mensaje en inglés de una contraseña nueva de bóveda demasiado corta indica
  ahora el mínimo real de 12 caracteres en lugar de quedar sin traducción.
- El filtro del webhook de alertas de manipulación rechaza además direcciones
  multicast, reservadas, de pruebas de rendimiento (`198.18.0.0/15`), de
  documentación y direcciones IPv6 que embeben un destino IPv4 (`::/96`,
  NAT64), sumadas a los rangos de loopback y privados.
- El sitio web del proyecto deja de fallar cuando el navegador bloquea
  `localStorage` e ignora un idioma almacenado que no reconoce.

### Modificado

- La activación por línea de comandos de bóvedas usa ahora un único analizador
  acotado de argumentos de Windows para abrir, actuar contextualmente y
  desmontar unidades.
- Actualizado el SDK de pruebas a 18.10.1 para las suites de Guardian y bóvedas.
- Actualizado Microsoft Windows App SDK a 2.5.1 tras superar la compilación
  x64, las pruebas de Guardian y bóvedas, y la validación visual de
  compatibilidad.
- Eliminadas 206 entradas duplicadas ya sustituidas y cuatro entradas obsoletas
  de longitud de contraseña del catálogo de localización en inglés. Se verificó
  que el catálogo efectivo no cambia salvo por las correcciones anteriores.

## 1.4.206 — 2026-09-19 (pre-release de desarrollo)

### Corregido

- El instalador mantiene el escudo de ProtectedApp tanto en el modo claro como
  en el oscuro de Windows, sin recurrir a la ilustración genérica de Inno Setup.
- La selección local de versión del instalador considera ahora también las
  etiquetas de pre-release de desarrollo, evitando asignar una versión anterior.

## 1.4.205 — 2026-09-19 (pre-release de desarrollo)

### Corregido

- El doble clic sobre un archivo `.pavault` abre ahora explícitamente esa
  bóveda sin desbloquear antes el panel de gestión de ProtectedApp. La acción
  contextual independiente de montar o desmontar se mantiene sin cambios.

## 1.4.204 — 2026-09-19 (pre-release de desarrollo)

### Añadido

- Publicada únicamente la parte pública del certificado de desarrollo/pruebas,
  con identidad y SHA-256 fijados, instrucciones explícitas de confianza para el
  usuario actual, retirada y respuesta a compromiso, y controles que rechazan
  cualquier sustitución o material de clave privada.
- Protección TPM opcional para la configuración y las reglas locales de
  ProtectedApp. Protege reglas, preferencias e historial de actividad mediante
  una clave no exportable del equipo actual.
- Segundo factor TPM opcional para cada bóveda. Una bóveda vinculada al TPM
  exige su contraseña y el TPM del equipo; al activarlo se crea una copia de
  recuperación solo con contraseña junto a la bóveda.
- Copias automáticas de configuración `.pabackup`, cifradas con contraseña, con
  carpeta de destino, frecuencia diaria o semanal, retención y acción manual
  **Crear ahora**.
- Campos de categoría, gravedad y resumen seguro en los eventos del webhook de
  manipulación, sin exponer rutas locales, credenciales ni errores sin filtrar.

### Modificado

- La contraseña maestra requiere ahora al menos 8 caracteres. Las contraseñas
  de bóvedas, copias cifradas y credenciales propias de aplicaciones mantienen
  un mínimo de 12 porque protegen directamente datos cifrados o portátiles.
- Acelerado el trabajo en unidades virtuales: las lecturas repetidas reutilizan
  una caché autenticada en memoria y el descriptor de la sesión, las búsquedas
  de archivos y bloques se indexan y los bloques claramente no comprimibles no
  consumen CPU intentando comprimirse.
- Reducidas aún más las asignaciones y copias de las bóvedas virtuales: las
  lecturas de Explorer y de la capa editable descifran directamente en el
  búfer de destino, los bloques en caché se copian sin un clon temporal
  completo y la primera edición de un bloque existente evita un segundo búfer
  de texto plano de 64 KiB.
- Mejorada la respuesta de carpetas virtuales grandes y del cierre de una
  bóveda. Los listados estables se reutilizan hasta que un cambio los invalida;
  la consolidación final reutiliza bloques modificados solo cuando la unidad
  virtual ya se ha detenido por completo, mientras los diarios de recuperación
  mantienen instantáneas independientes.
- La consolidación final secuencial ya no llena la caché interactiva de texto
  plano con bloques que no se volverán a leer, reduciendo la presión de memoria
  y conservando la caché autenticada para el uso normal de Explorer.
- La sesión validada de una bóveda se reutiliza al montar inmediatamente la
  unidad virtual, evitando una segunda derivación de contraseña y operación TPM
  en aperturas normales.
- Los cierres ordinarios de manejadores de Explorador se agrupan antes de crear
  el diario. Los `Flush` explícitos y el bloqueo final siguen esperando un
  diario cifrado durable.
- Los diarios completos usados habitualmente durante la edición se sustituyen
  por un diario diferencial cifrado y autenticado que conserva solo metadatos y
  bloques modificados de 64 KiB. Las modificaciones demasiado grandes recurren
  automáticamente al diario completo compatible y un diario interrumpido puede
  consultarse en solo lectura antes de consolidarlo.
- Las carpetas virtuales editables indexan sus hijos directos, evitando
  recorrer toda la bóveda cada vez que Explorer enumera una carpeta.
- Las escrituras de contenedores cifrados temporales se agrupan en memoria y
  siguen vaciándose a almacenamiento físico antes de verificarse y sustituirse
  atómicamente.
- Aclarado el texto de TPM en la interfaz de Configuración y la documentación:
  la protección TPM de configuración protege reglas locales y aplicaciones
  protegidas; no modifica el cifrado de una bóveda salvo que se active TPM en
  esa bóveda concreta.
- Las operaciones TPM costosas se ejecutan fuera del hilo de interfaz para que
  los cambios de configuración sigan respondiendo.
- Las pruebas completas del instalador ya no se ejecutan en cada *push* o
  solicitud de cambios: se reservan para los candidatos de revisión etiquetados.
- El editor para añadir una aplicación protegida se reorganizó en dos columnas
  en ventanas anchas, evitando desplazamiento vertical innecesario.

### Corregido

- Un cierre por inactividad o por tiempo de un minuto avisa ahora 15 segundos
  antes de cerrar, y no inmediatamente al arrancar ni al ampliar el plazo.
- Al descartar un aviso de inactividad, el movimiento del puntero necesario
  para llegar al diálogo ya no amplía el plazo por accidente; la opción ahora
  se llama **No ampliar**.
- El selector de aplicaciones protegidas ahora detecta programas de escritorio
  también desde App Paths y los accesos del menú Inicio, y excluye la utilidad
  interna de Dokan.
- El selector de aplicaciones protegidas se adapta a ventanas estrechas y DPI
  altos para que ni el buscador ni las filas se corten por la derecha.
- Las subcarpetas inaccesibles del menú Inicio se omiten al detectar
  aplicaciones, para que una carpeta protegida no pueda cerrar ProtectedApp.
- Se ignoran únicamente los relanzamientos inmediatos de procesos auxiliares
  tras un cierre automático, evitando que el temporizador muestre una petición
  de contraseña no solicitada. Los nuevos inicios autorizados parten de un
  estado de temporizador limpio.
- Se mantiene visible y activo el tiempo de espera por contraseñas erróneas al
  cerrar y volver a abrir su ventana, en lugar de aparentar que el bloqueo se
  reinicia.
- El instalador abre la creación de la contraseña maestra en una instalación nueva; las actualizaciones mantienen el panel en segundo plano. También funciona si ProtectedApp ya estaba en ejecución.
- La instalación, actualización y desinstalación de Guardian gestionan ahora su tarea de vigilancia SYSTEM mediante la API local del Programador de tareas en lugar de CIM, que puede denegar el acceso en Windows Sandbox.
- Se muestra el error real del instalador de Guardian antes de intentar crear la política contra un servicio que siguió en ejecución, evitando el aviso engañoso de autorización inválida.
- La reparación omite la reconfiguración innecesaria del servicio si su ejecutable y arranque automático ya son correctos; cuando Windows rechaza un cambio necesario se conserva el error concreto.
- Se evita la alerta prematura «Guardian no disponible» mientras siguen en curso la contraseña maestra del primer inicio y la instalación automática de Guardian.
- Se instala Guardian al abrir interactivamente por primera vez una instancia que inició el setup en segundo plano; esa apertura no vuelve a ejecutar el flujo de instalación del arranque.
- Bloqueos de la interfaz al convertir una bóveda a protección TPM y al ejecutar
  la comprobación de copias de bóveda del instalador.
- La versión automática del instalador local tiene en cuenta la versión del
  proyecto, los tags publicados, los binarios instalados y los instaladores ya
  generados, evitando crear un downgrade o reutilizar una versión pública de la
  extensión del Explorador.

## 1.4.186 — 2026-09-13 (candidato de revisión sin firmar)

### Añadido

- Copias automáticas cifradas de la configuración y guía de recuperación.
- Protección TPM opcional para bóvedas y para el estado de configuración local.
- Eventos de webhook de manipulación más útiles y respetuosos con la privacidad.

### Modificado

- Rediseño de la interfaz WinUI inspirado en el espacio de trabajo ShieldLock:
  navegación, tarjetas de inicio, tipografías, iconos, diálogos, editores,
  filas de bóveda y superficies adaptables de Configuración.
- Estilo acrílico translúcido para navegación y superposiciones de diálogo, con
  mejoras de foco, *hover*, selección y contraste.
- Editor de aplicaciones protegidas más ancho, centrado y con menos recortes de
  iconos o desplazamiento vertical innecesario.
- Los instaladores locales de desarrollo reciben una versión nueva automática y
  pueden usar el flujo de firma local configurado explícitamente.

### Corregido

- Problemas de diseño, centrado, recorte y tamaño de iconos de acción en cuadros
  emergentes introducidos durante el rediseño.
- Bloqueo de la comprobación del instalador por el contexto de sincronización de
  interfaz al leer bóvedas.
- Carga de los ejecutores de CI: las comprobaciones pesadas se ejecutan solo en
  candidatos de revisión etiquetados.

## Primera publicación de código abierto — 2026-09-10

### Añadido

- Repositorio público GPL-3.0-or-later, instrucciones de compilación, guía de
  contribución, política de seguridad, política de firma, proceso de versiones,
  flujo de SBOM/procedencia y comprobaciones de seguridad del repositorio.
- Documentación en inglés junto a la documentación original en español.

### Seguridad

- Refuerzo de la validación del instalador, confianza de actualizaciones,
  protección de configuración, recuperación y controles frente a secretos en el
  repositorio.
- Documentación del modelo de seguridad, límites de privacidad, webhook
  opcional y responsabilidades de firma de código.

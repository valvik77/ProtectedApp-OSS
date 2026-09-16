# Historial de cambios

Aquí se documentan los cambios relevantes de ProtectedApp. Las entradas se
agrupan por versión o candidato de revisión que las contiene. Un **candidato de
revisión sin firmar** no es una versión pública y no debe distribuirse como tal;
consulta [RELEASING.md](RELEASING.md) para conocer el proceso de publicación y
firma.

> [English](CHANGELOG.md)

## Pendiente de publicación

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

- Acelerado el trabajo en unidades virtuales: las lecturas repetidas reutilizan
  una caché autenticada en memoria y el descriptor de la sesión, las búsquedas
  de archivos y bloques se indexan y los bloques claramente no comprimibles no
  consumen CPU intentando comprimirse.
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

### Corregido

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

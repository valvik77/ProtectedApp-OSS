using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ProtectedApp.Services;

/// <summary>
/// Central catalogue for the user interface.  The Spanish source text remains
/// the fallback language while English is selected explicitly or from Windows.
/// New visible strings should be added here rather than hard-coded per locale.
/// </summary>
public static class LocalizationService
{
    // The catalogue has grown incrementally over several screens.  Accepting
    // a later entry for an existing Spanish source prevents a duplicate entry
    // from crashing the whole UI during static initialization.  The source is
    // still preserved per control by ElementSources, so this does not re-open
    // the reverse-translation ambiguity that existed before.
    private sealed class TranslationDictionary : Dictionary<string, string>
    {
        public TranslationDictionary(IEqualityComparer<string> comparer) : base(comparer) { }
        public new void Add(string key, string value) => this[key] = value;
    }

    private static readonly IReadOnlyDictionary<string, string> English = new TranslationDictionary(StringComparer.Ordinal)
    {
        ["Inicio"] = "Home",
        ["Resumen de la protección de este equipo"] = "A summary of protection on this computer",
        ["Protección local"] = "Local protection",
        ["ESPACIO DE TRABAJO"] = "WORKSPACE",
        ["Resumen de protección"] = "Protection overview",
        ["Centro de seguridad"] = "Security center",
        ["Consulta el estado de tus reglas, bóvedas y actividad reciente."] = "Review the status of your rules, vaults, and recent activity.",
        ["PROTECCIÓN ACTIVA"] = "PROTECTION ACTIVE",
        ["Guardian conectado"] = "Guardian connected",
        ["Guardian requiere atención"] = "Guardian needs attention",
        ["Protección administrada"] = "Managed protection",
        ["REGLAS ACTIVAS"] = "ACTIVE RULES",
        ["aplicaciones protegidas"] = "protected applications",
        ["BÓVEDAS"] = "VAULTS",
        ["contenedores cifrados"] = "encrypted containers",
        ["ACTIVIDAD · 24 H"] = "ACTIVITY · 24 H",
        ["eventos registrados"] = "recorded events",
        ["INTEGRIDAD"] = "INTEGRITY",
        ["Pendiente"] = "Pending",
        ["Disponible"] = "Available",
        ["comprobación del sistema"] = "system check",
        ["Acciones rápidas"] = "Quick actions",
        ["Gestiona la protección sin abandonar el panel principal."] = "Manage protection without leaving the main panel.",
        ["Gestionar aplicaciones"] = "Manage applications",
        ["Estado del sistema"] = "System status",
        ["La comprobación detallada está disponible en Diagnóstico."] = "The detailed check is available in Diagnostics.",
        ["Consulta Diagnóstico para revisar el estado de los componentes."] = "Open Diagnostics to review component status.",
        ["Abrir diagnóstico"] = "Open diagnostics",
        ["Bloqueo inmediato"] = "Immediate lock",
        ["Aplicaciones protegidas"] = "Protected applications",
        ["Controla qué programas necesitan autorización"] = "Control which programs require authorization",
        ["Bóvedas cifradas"] = "Encrypted vaults",
        ["Protege archivos dentro de contenedores con contraseña"] = "Protect files inside password-protected containers",
        ["Actividad"] = "Activity",
        ["Consulta el historial de seguridad del equipo"] = "Review this computer's security history",
        ["Diagnóstico"] = "Diagnostics",
        ["Comprueba que todas las capas de protección funcionan"] = "Check that all protection layers are working",
        ["Configuración"] = "Settings",
        ["Ajusta la protección y las credenciales"] = "Adjust protection and credentials",
        ["Acerca de"] = "About",
        ["Información y componentes de ProtectedApp"] = "ProtectedApp information and components",
        ["General"] = "General",
        ["Seguridad"] = "Security",
        ["Mantenimiento"] = "Maintenance",
        ["Información"] = "Information",
        ["SESIÓN Y PANEL"] = "SESSION AND PANEL",
        ["BÓVEDAS"] = "VAULTS",
        ["GENERAL"] = "GENERAL",
        ["SEGURIDAD"] = "SECURITY",
        ["DATOS Y MANTENIMIENTO"] = "DATA AND MAINTENANCE",
        ["COPIAS Y RECUPERACIÓN"] = "BACKUPS AND RECOVERY",
        ["AVISOS Y BLOQUEO"] = "NOTIFICATIONS AND LOCKING",
        ["SERVICIO Y ACTUALIZACIONES"] = "SERVICE AND UPDATES",
        ["Iniciar con Windows"] = "Start with Windows",
        ["Activa la protección al iniciar sesión."] = "Enable protection when you sign in.",
        ["Icono en la bandeja"] = "Tray icon",
        ["La interfaz seguirá accesible desde Inicio."] = "The interface will remain accessible from Start.",
        ["Bloqueo automático"] = "Automatic lock",
        ["Bloquea el panel tras inactividad."] = "Lock the panel after inactivity.",
        ["Bloquear al cerrar"] = "Lock when closed",
        ["Exige de nuevo la contraseña maestra."] = "Require the master password again.",
        ["Idioma de la aplicación"] = "App language",
        ["Elige el idioma de la interfaz y los avisos."] = "Choose the language for the interface and notifications.",
        ["Sistema"] = "System",
        ["Español"] = "Spanish",
        ["Abrir bóvedas en consulta"] = "Open vaults for viewing",
        ["El doble clic abre en solo lectura; puedes editar desde el panel."] = "Double-click opens read-only; you can edit from the panel.",
        ["Letra de unidad de bóveda"] = "Vault drive letter",
        ["Automática"] = "Automatic",
        ["Se usará si está libre; si no, ProtectedApp elegirá otra automáticamente."] = "It will be used if available; otherwise ProtectedApp will choose another automatically.",
        ["Velocidad de detección"] = "Detection speed",
        ["Intervalo del barrido de respaldo."] = "Background scan interval.",
        ["Inmediata · 200 ms"] = "Immediate · 200 ms",
        ["Equilibrada · 500 ms"] = "Balanced · 500 ms",
        ["Ligera · 1000 ms"] = "Light · 1000 ms",
        ["Windows Hello (PIN / Biometría)"] = "Windows Hello (PIN / Biometrics)",
        ["Contraseña maestra"] = "Master password",
        ["Panel y desinstalación."] = "Panel and uninstall.",
        ["Cambiar"] = "Change",
        ["Copia cifrada"] = "Encrypted backup",
        ["Reglas, preferencias e historial."] = "Rules, preferences and history.",
        ["Exportar…"] = "Export…",
        ["Limpiar"] = "Clear",
        ["Activado"] = "Enabled",
        ["Desactivado"] = "Disabled",
        ["Nunca"] = "Never",
        ["1 minuto"] = "1 minute",
        ["30 minutos"] = "30 minutes",
        ["Restaurar…"] = "Restore…",
        ["Copias de bóvedas"] = "Vault backups",
        ["Configurar…"] = "Configure…",
        ["Crear ahora"] = "Create now",
        ["Abrir carpeta"] = "Open folder",
        ["Limpiar antiguas"] = "Clean up old backups",
        ["Avisos de bóvedas"] = "Vault notifications",
        ["Avisos de Windows si una copia vence, falla o se recupera."] = "Windows notifications if a backup expires, fails or recovers.",
        ["Avisos de cierre automático"] = "Automatic closing notifications",
        ["Permite avisar y ampliar los cierres por tiempo o inactividad."] = "Allow warnings and extensions for time or inactivity closures.",
        ["Alertas de seguridad"] = "Security alerts",
        ["Avisa si Guardian deja de estar disponible o se detecta manipulación."] = "Warn if Guardian becomes unavailable or tampering is detected.",
        ["Bloqueo inmediato"] = "Immediate lock",
        ["Cierra accesos protegidos, desmonta bóvedas y bloquea Windows."] = "Closes protected access, unmounts vaults and locks Windows.",
        ["Bloquear…"] = "Lock…",
        ["Atajo de bloqueo inmediato"] = "Immediate lock shortcut",
        ["Servicio Guardian"] = "Guardian service",
        ["Alerta remota de manipulación"] = "Remote tamper alert",
        ["Actualización manual"] = "Manual update",
        ["Seleccionar…"] = "Select…",
        ["Desinstalar"] = "Uninstall",
        ["Desinstalar…"] = "Uninstall…",
        ["Aplicaciones"] = "Applications",
        ["Buscar aplicaciones"] = "Search applications",
        ["Activas"] = "Enabled",
        ["Todos"] = "All",
        ["Acciones de grupo"] = "Group actions",
        ["APLICACIÓN"] = "APPLICATION",
        ["ACCESO"] = "ACCESS",
        ["ACTIVA"] = "ENABLED",
        ["Añadir"] = "Add",
        ["No hay aplicaciones protegidas"] = "No protected applications",
        ["Pulsa Añadir para seleccionar una aplicación, un ejecutable o un script."] = "Select Add to choose an application, executable or script.",
        ["Nueva bóveda"] = "New vault",
        ["Convertir carpeta"] = "Convert folder",
        ["Importar"] = "Import",
        ["Limpiar ausentes"] = "Clean missing",
        ["Contenedores cifrados con contraseña propia"] = "Password-protected encrypted containers",
        ["Consulta archivos sin crear una copia completa en el disco o abre la bóveda para editar y guardar cambios."] = "Browse files without creating a complete local copy, or open the vault to edit and save changes.",
        ["BÓVEDA"] = "VAULT",
        ["UBICACIÓN DEL ARCHIVO"] = "FILE LOCATION",
        ["ESTADO"] = "STATUS",
        ["Buscar en el historial"] = "Search activity history",
        ["Exportar actividad"] = "Export activity",
        ["Exportar los eventos visibles"] = "Export visible events",
        ["Limpiar historial"] = "Clear history",
        ["EVENTOS · 24 H"] = "EVENTS · 24 H",
        ["BLOQUEOS"] = "LOCKS",
        ["CONTRASEÑAS FALLIDAS"] = "FAILED PASSWORDS",
        ["AVISOS Y ERRORES"] = "WARNINGS AND ERRORS",
        ["Mostrar eventos de las últimas 24 horas"] = "Show events from the last 24 hours",
        ["Filtrar eventos de las últimas 24 horas"] = "Filter events from the last 24 hours",
        ["Mostrar bloqueos"] = "Show locks",
        ["Filtrar bloqueos"] = "Filter locks",
        ["Mostrar contraseñas fallidas"] = "Show failed passwords",
        ["Filtrar contraseñas fallidas"] = "Filter failed passwords",
        ["Mostrar avisos y errores"] = "Show warnings and errors",
        ["Filtrar avisos y errores"] = "Filter warnings and errors",
        ["Aún no hay actividad"] = "No activity yet",
        ["No hay eventos que coincidan con el filtro"] = "No events match the current filter",
        ["Comprobar ahora"] = "Check now",
        ["Diagnóstico todavía no ejecutado"] = "Diagnostics have not been run yet",
        ["Comprueba los componentes de protección sin modificar el sistema."] = "Check protection components without changing the system.",
        ["Pulsa Comprobar ahora para analizar ProtectedApp"] = "Select Check now to analyze ProtectedApp",
        ["Reparar protección"] = "Repair protection",
        ["Comprobando…"] = "Checking…",
        ["Comprobando la protección"] = "Checking protection",
        ["Consultando Guardian y los componentes instalados…"] = "Checking Guardian and installed components…",
        ["Todas las comprobaciones son correctas"] = "All checks passed",
        ["No se pudo completar el diagnóstico"] = "Could not complete diagnostics",
        ["Comprobar de nuevo"] = "Check again",
        ["Reparando componentes de protección"] = "Repairing protection components",
        ["Windows solicitará permiso de administrador para reinstalar las capas protegidas."] = "Windows will ask for administrator permission to reinstall the protection components.",
        ["CÓMO FUNCIONA PROTECTEDAPP"] = "HOW PROTECTEDAPP WORKS",
        ["RECOMENDACIÓN"] = "RECOMMENDATION",
        ["Contrato de licencia"] = "License agreement",
        ["Licencia GPL"] = "GPL license",
        ["Licencias de terceros"] = "Third-party licenses",
        ["Desarrollador"] = "Developer",
        ["Plataforma"] = "Platform",
        ["Protección local gratuita para aplicaciones y bóvedas"] = "Free local protection for applications and vaults",
        ["Uso gratuito, personal y profesional"] = "Free for personal and professional use",
        ["Versión"] = "Version",
        ["INFORMACIÓN"] = "INFORMATION",
        ["COMPONENTES"] = "COMPONENTS",
        ["Guardian aplica las reglas protegidas como SYSTEM. Las bóvedas cifradas se montan mediante Dokany y protegen el contenido mediante cifrado autenticado."] = "Guardian applies protected rules as SYSTEM. Encrypted vaults are mounted through Dokany and protect their contents with authenticated encryption.",
        ["© 2026 Valvik. ProtectedApp complementa la seguridad de Windows y no sustituye las políticas empresariales."] = "© 2026 Valvik. ProtectedApp complements Windows security and does not replace organizational security policies.",
        ["PROYECTO"] = "PROJECT",
        ["Bloquear"] = "Lock",
        ["Activa"] = "Active",
        ["Usa la contraseña maestra"] = "Uses the master password",
        ["Contraseña propia"] = "Custom password",
        ["Todos los días"] = "Every day",
        ["Lun-Vie"] = "Mon–Fri",
        ["Fin de semana"] = "Weekend",
        ["Ninguno"] = "None",
        ["bloqueo fuera de horario"] = "locked outside schedule",
        ["confianza"] = "grace",
        ["hasta cerrar"] = "until closed",
        ["cierre"] = "close",
        ["inactividad"] = "inactivity",
        ["horario con bloqueo"] = "scheduled lock",
        ["horario"] = "schedule",
        ["Desbloquear ProtectedApp"] = "Unlock ProtectedApp",
        ["Autorizar nueva bóveda"] = "Authorize new vault",
        ["Introduce tu contraseña maestra para continuar"] = "Enter your master password to continue",
        ["Introduce la contraseña para continuar"] = "Enter the password to continue",
        ["Ver historial"] = "View history",
        ["Comprobar integridad"] = "Check integrity",
        ["Copias programadas"] = "Scheduled backups",
        ["Copia de recuperación"] = "Recovery backup",
        ["Editar bóveda"] = "Edit vault",
        ["Quitar de la lista"] = "Remove from list",
        ["Copias no disponibles"] = "Backups unavailable",
        ["Guarda y bloquea la bóveda y configura un destino de copias antes de continuar."] = "Save and lock the vault, then configure a backup destination before continuing.",
        ["Guardar y bloquear"] = "Save and lock",
        ["Actualizar"] = "Refresh",
        ["No hay bóvedas cifradas"] = "No encrypted vaults",
        ["Crea un contenedor nuevo o importa uno existente."] = "Create a new container or import an existing one.",
        ["Abrir"] = "Open",
        ["Ver archivos"] = "Browse files",
        ["Editar"] = "Edit",
        ["Eliminar permanentemente"] = "Delete permanently",
        ["Gestión de bóveda"] = "Vault management",
        ["Gestionar bóveda"] = "Manage vault",
        ["Historial de bóveda"] = "Vault history",
        ["Historial de copias programadas"] = "Scheduled backup history",
        ["No hay copias pendientes"] = "No pending backups",
        ["No hay referencias ausentes"] = "No missing references",
        ["No hay versiones"] = "No versions",
        ["Sin versiones"] = "No versions",
        ["Bóveda abierta"] = "Vault is open",
        ["Guarda y bloquea la bóveda antes de quitarla."] = "Save and lock the vault before removing it.",
        ["Guarda y bloquea la bóveda antes de editarla o cambiar su contraseña."] = "Save and lock the vault before editing it or changing its password.",
        ["Guarda y bloquea la bóveda antes de comprobar su integridad."] = "Save and lock the vault before checking its integrity.",
        ["Copia no compatible"] = "Incompatible backup",
        ["Bóveda duplicada"] = "Duplicate vault",
        ["Importar bóveda"] = "Import vault",
        ["Introduce la contraseña del contenedor"] = "Enter the container password",
        ["Introduce la contraseña actual para editar la bóveda"] = "Enter the current password to edit the vault",
        ["Introduce la contraseña para abrir la bóveda"] = "Enter the password to open the vault",
        ["Contraseña de la bóveda"] = "Vault password",
        ["Bloquear automáticamente después de (minutos)"] = "Lock automatically after (minutes)",
        ["Desmontar tras inactividad (0 para desactivar)"] = "Unmount after inactivity (0 to disable)",
        ["Descripción"] = "Description",
        ["Cambiar ubicación…"] = "Change location…",
        ["Abrir bóveda"] = "Open vault",
        ["Copia de seguridad"] = "Backup",
        ["No se encontraron aplicaciones"] = "No applications found",
        ["Buscar aplicaciones instaladas"] = "Search installed applications",
        ["aplicaciones de escritorio detectadas"] = "desktop applications detected",
        ["Reemplazar configuración"] = "Replace configuration",
        ["Restaurar copia"] = "Restore backup",
        ["Copia creada"] = "Backup created",
        ["Copia restaurada"] = "Backup restored",
        ["Actividad exportada"] = "Activity exported",
        ["No hay eventos"] = "No events",
        ["No se pudo exportar"] = "Could not export",
        ["Añadir aplicación"] = "Add application",
        ["Proteger seleccionada"] = "Protect selected",
        ["Elegir archivo…"] = "Choose file…",
        ["Editar protección"] = "Edit protection",
        ["Bloquear esta aplicación"] = "Lock this application",
        ["Bloquear aplicación"] = "Lock application",
        ["Eliminar protección"] = "Remove protection",
        ["Eliminar"] = "Delete",
        ["Grupo bloqueado"] = "Group locked",
        ["Sin aplicaciones"] = "No applications",
        ["Sin cambios"] = "No changes",
        ["Nombre"] = "Name",
        ["Grupo o categoría"] = "Group or category",
        ["Mantener configuración actual"] = "Keep current settings",
        ["Usar contraseña maestra"] = "Use master password",
        ["Definir contraseña propia"] = "Set custom password",
        ["Nueva contraseña propia"] = "New custom password",
        ["Confirmar contraseña"] = "Confirm password",
        ["No se pudo verificar la contraseña."] = "Could not verify the password.",
        ["Demasiados intentos."] = "Too many attempts.",
        ["Inténtalo de nuevo en"] = "Try again in",
        ["segundos"] = "seconds",
        ["Carpeta no disponible"] = "Folder unavailable",
        ["La carpeta seleccionada ya no existe."] = "The selected folder no longer exists.",
        ["Convertir carpeta en bóveda"] = "Convert folder to vault",
        ["Bóveda verificada"] = "Vault verified",
        ["Conversión pendiente"] = "Conversion pending",
        ["No se pudo convertir"] = "Could not convert",
        ["No se pudo crear y verificar la bóveda cifrada."] = "Could not create and verify the encrypted vault.",
        ["La contraseña de la bóveda debe tener al menos 8 caracteres."] = "The vault password must contain at least 8 characters.",
        ["No se pudo descartar"] = "Could not discard",
        ["Descartar trabajo recuperable"] = "Discard recoverable work",
        ["No se pudo abrir la carpeta"] = "Could not open folder",
        ["No se pudo restaurar la copia"] = "Could not restore backup",
        ["No se pudo abrir la copia"] = "Could not open backup",
        ["No se pudo crear la copia"] = "Could not create backup",
        ["Hay bóvedas abiertas"] = "Vaults are open",
        ["Proteger copia"] = "Protect backup",
        ["Abrir copia cifrada"] = "Open encrypted backup",
        ["Contraseña de la copia"] = "Backup password",
        ["Configurar copias de bóvedas"] = "Configure vault backups",
        ["Activar copias programadas"] = "Enable scheduled backups",
        ["No se ha elegido una carpeta."] = "No folder has been selected.",
        ["Frecuencia"] = "Frequency",
        ["Cada día"] = "Every day",
        ["Cada semana"] = "Every week",
        ["Versiones por bóveda"] = "Versions per vault",
        ["Destino"] = "Destination",
        ["Las bóvedas abiertas se omiten. Cada copia es un contenedor .pavault completo e independiente."] = "Open vaults are skipped. Each backup is a complete, independent .pavault container.",
        ["Elegir carpeta…"] = "Choose folder…",
        ["Nueva contraseña (opcional)"] = "New password (optional)",
        ["Déjalo vacío para conservarla"] = "Leave empty to keep it",
        ["Confirmar nueva contraseña"] = "Confirm new password",
        ["Copia cifrada anterior disponible"] = "Previous encrypted backup available",
        ["Contenedor actual"] = "Current container",
        ["Copia anterior"] = "Previous backup",
        ["La comprobación estructural no descifra archivos. La contraseña verificará la autenticidad antes de restaurar."] = "The structural check does not decrypt files. The password will verify authenticity before restoring.",
        ["Eliminar copia"] = "Delete backup",
        ["Introduce la contraseña para comprobar todos los bloques cifrados"] = "Enter the password to check all encrypted blocks",
        ["Todavía no hay eventos registrados para esta bóveda."] = "There are no events recorded for this vault yet.",
        ["Autorizar importación de bóveda"] = "Authorize vault import",
        ["Bóveda cifrada creada"] = "Encrypted vault created",
        ["Bóveda cifrada importada"] = "Encrypted vault imported",
        ["No se pudo guardar y desmontar la bóveda."] = "Could not save and unmount the vault.",
        ["Bóveda añadida mediante doble clic"] = "Vault added by double-click",
        ["Contraseña incorrecta al importar la bóveda"] = "Incorrect password while importing the vault",
        ["La carpeta de trabajo ya no existe o Windows no permitió abrirla."] = "The working folder no longer exists or Windows did not allow it to be opened.",
        ["Carpeta de recuperación abierta para revisión manual"] = "Recovery folder opened for manual review",
        ["Escribe DESCARTAR para habilitar la eliminación."] = "Type DISCARD to enable deletion.",
        ["Autorizar descarte de recuperación"] = "Authorize recovery discard",
        ["Windows no permitió eliminar la carpeta."] = "Windows did not allow the folder to be deleted.",
        ["Trabajo recuperable descartado con autenticación maestra y confirmación explícita"] = "Recoverable work discarded with master authentication and explicit confirmation",
        ["La copia anterior es válida, pero también lo es el contenedor actual. ¿Quieres volver deliberadamente a la versión anterior?"] = "The previous backup is valid, but so is the current container. Do you want to deliberately return to the previous version?",
        ["El contenedor principal no se pudo validar con esta contraseña, pero su copia anterior sí. ¿Quieres restaurarla ahora?"] = "The primary container could not be validated with this password, but its previous backup could. Restore it now?",
        ["Copia cifrada anterior restaurada automáticamente al abrir la bóveda"] = "Previous encrypted backup restored automatically when opening the vault",
        ["Bóveda retirada de la lista; el contenedor cifrado se conservó"] = "Vault removed from the list; the encrypted container was kept",
        ["Gestionar referencias de bóvedas"] = "Manage vault references",
        ["sin ubicación"] = "no location",
        ["Eliminar bóveda permanentemente"] = "Delete vault permanently",
        ["Autorizar eliminación permanente de"] = "Authorize permanent deletion of",
        ["Windows rechazó la eliminación del contenedor."] = "Windows rejected deletion of the container.",
        ["Eliminación parcial"] = "Partial deletion",
        ["El contenedor se eliminó, pero alguna copia se conservó."] = "The container was deleted, but one or more backups were kept.",
        ["La unidad virtual continúa disponible para evitar perder cambios."] = "The virtual drive remains available to avoid losing changes.",
        ["Autorizar eliminación de la copia anterior"] = "Authorize previous backup deletion",
        ["Windows rechazó la eliminación."] = "Windows rejected the deletion.",
        ["Copia cifrada anterior eliminada con autorización maestra"] = "Previous encrypted backup deleted with master authorization",
        ["Contraseña incorrecta al editar la bóveda"] = "Incorrect password while editing the vault",
        ["Las nuevas contraseñas no coinciden."] = "The new passwords do not match.",
        ["El contenedor no se modificó y conserva su contraseña actual."] = "The container was not modified and keeps its current password.",
        ["Configuración actualizada, pero no se pudo cambiar la contraseña de la bóveda"] = "Settings updated, but the vault password could not be changed",
        ["Los demás cambios se guardaron, pero la contraseña anterior sigue siendo válida."] = "Other changes were saved, but the previous password is still valid.",
        ["Configuración de la bóveda actualizada"] = "Vault settings updated",
        ["Configuración y contraseña de la bóveda actualizadas"] = "Vault settings and password updated",
        ["estructura válida"] = "valid structure",
        ["estructura dañada"] = "damaged structure",
        ["Guarda y bloquea la bóveda antes de gestionar su copia anterior."] = "Save and lock the vault before managing its previous backup.",
        ["No existe una copia cifrada anterior para esta bóveda."] = "No previous encrypted backup exists for this vault.",
        ["Introduce la contraseña de la bóveda para validar la copia anterior"] = "Enter the vault password to validate the previous backup",
        ["Contraseña incorrecta al validar la copia anterior de la bóveda"] = "Incorrect password while validating the vault's previous backup",
        ["El contenedor principal no superó la autenticación, pero su copia cifrada anterior sí"] = "The primary container did not pass authentication, but its previous encrypted backup did",
        ["La copia se conservó sin cambios."] = "The backup was kept unchanged.",
        ["No se encontró ninguna copia programada para esta bóveda."] = "No scheduled backup was found for this vault.",
        ["Selecciona una copia completa de esta bóveda."] = "Select a complete backup of this vault.",
        ["Comprobar valida la contraseña y autenticidad sin modificar la bóveda. Tras una comprobación correcta podrás decidir si restaurarla."] = "Check validates the password and authenticity without modifying the vault. After a successful check, you can decide whether to restore it.",
        ["Eliminar versión de copia"] = "Delete backup version",
        ["La versión se conservó. Comprueba que el archivo no esté abierto ni protegido."] = "The version was kept. Check that the file is not open or protected.",
        ["Contraseña incorrecta al validar una versión de copia programada"] = "Incorrect password while validating a scheduled backup version",
        ["Contraseña incorrecta al comprobar la integridad de la bóveda"] = "Incorrect password while checking vault integrity",
        ["La comprobación de integridad detectó bloques no válidos"] = "The integrity check detected invalid blocks",
        ["La copia cifrada anterior se ha autenticado correctamente. Restaurarla sustituirá el contenedor actual; este se conservará con la marca 'corrupt' para no perder evidencia."] = "The previous encrypted backup was authenticated successfully. Restoring it will replace the current container; it will be kept with the 'corrupt' marker to preserve evidence.",
        ["Bóveda recuperada desde la copia anterior tras comprobar la integridad"] = "Vault recovered from the previous backup after the integrity check",
        ["La copia anterior se restauró correctamente como contenedor principal."] = "The previous backup was restored successfully as the primary container.",
        ["No se pudieron cerrar todas las bóvedas"] = "Could not close all vaults",
        ["No se encuentra la bóveda"] = "Vault not found",
        ["El archivo cifrado fue movido o eliminado. Quítalo de la lista y vuelve a importarlo desde su nueva ubicación."] = "The encrypted file was moved or deleted. Remove it from the list and import it again from its new location.",
        ["La bóveda continúa disponible en su unidad de consulta."] = "The vault remains available on its read-only drive.",
        ["La bóveda continúa abierta para editar."] = "The vault remains open for editing.",
        ["Bóveda pendiente de recuperación"] = "Vault recovery pending",
        ["Revisar bóvedas"] = "Review vaults",
        ["Activar webhook de manipulación"] = "Enable tamper webhook",
        ["Identificador de instalación"] = "Installation identifier",
        ["Secreto HMAC (vacío conserva el actual)"] = "HMAC secret (empty keeps the current one)",
        ["Solo se envían códigos de manipulación y recuperación fallida. Es una alerta informativa; un administrador podría desactivarla junto con Guardian."] = "Only tamper and failed-recovery codes are sent. This is an informational alert; an administrator could disable it together with Guardian.",
        ["Webhook de manipulación configurado"] = "Tamper webhook configured",
        ["Webhook de manipulación desactivado"] = "Tamper webhook disabled",
        ["Configura primero una carpeta para las copias de bóvedas."] = "Configure a folder for vault backups first.",
        ["Todas las bóvedas ya conservan solo las versiones configuradas."] = "All vaults already retain only the configured number of versions.",
        ["Copias programadas de bóvedas configuradas"] = "Scheduled vault backups configured",
        ["Copias programadas de bóvedas desactivadas"] = "Scheduled vault backups disabled",
        ["No hay bóvedas cerradas disponibles para copiar ahora."] = "There are no closed vaults available to back up now.",
        ["Copia programada de bóveda creada"] = "Scheduled vault backup created",
        ["Este cambio no modifica las contraseñas propias de las aplicaciones."] = "This change does not modify custom application passwords.",
        ["La nueva contraseña maestra ya está activa."] = "The new master password is active.",
        ["La autenticación se ha cerrado de forma segura porque el motor protegido no respondió."] = "Authentication was closed safely because the protected engine did not respond.",
        ["No se puede bloquear una aplicación mientras el servicio Guardian no esté activo. Repara el servicio desde Configuración."] = "An application cannot be locked while the Guardian service is not active. Repair the service from Settings.",
        ["No se puede bloquear el grupo mientras el servicio Guardian no esté activo. Repara el servicio desde Configuración."] = "The group cannot be locked while the Guardian service is not active. Repair the service from Settings.",
        ["Todas las aplicaciones del grupo ya están activas."] = "All applications in the group are already enabled.",
        ["Protección eliminada"] = "Protection removed",
        ["Protección activada"] = "Protection enabled",
        ["Protección desactivada"] = "Protection disabled",
        ["Protección añadida"] = "Protection added",
        ["No se pudo aplicar el cambio de protección. Repara o desbloquea Guardian e inténtalo de nuevo."] = "Could not apply the protection change. Repair or unlock Guardian and try again.",
        ["La protección de carpetas requiere que el servicio Guardian esté instalado, actualizado y en ejecución."] = "Folder protection requires the Guardian service to be installed, up to date, and running.",
        ["Alerta: Guardian no está disponible; la protección administrada necesita reparación"] = "Alert: Guardian is unavailable; managed protection needs repair",
        ["Guardian no está disponible. Repara el servicio para restaurar la protección."] = "Guardian is unavailable. Repair the service to restore protection.",
        ["Cierre normal solicitado; guarda los cambios pendientes si la aplicación lo requiere"] = "Normal closing requested; save pending changes if the application requires it",
        ["Ampliación del cierre automático cancelada"] = "Automatic closing extension cancelled",
        ["Guardian bloqueó la ejecución de forma segura."] = "Guardian blocked the launch safely.",
        ["Se detectó una manipulación de Guardian. Se han revocado los accesos protegidos."] = "Guardian tampering was detected. Protected access has been revoked.",
        ["Solicitando instalación…"] = "Requesting installation…",
        ["Solicitando desinstalación…"] = "Requesting uninstallation…",
        ["Recuperar configuración protegida"] = "Recover protected settings",
        ["Introduce la contraseña maestra para restaurar las reglas desde Guardian"] = "Enter the master password to restore rules from Guardian",
        ["Guardian no devolvió la política protegida."] = "Guardian did not return the protected policy.",
        ["Configuración local restaurada desde la política protegida"] = "Local settings restored from the protected policy",
        ["Se han exportado"] = "Exported",
        ["No hay actividad visible para exportar con los filtros actuales."] = "There is no visible activity to export with the current filters.",
        ["Limpiar historial"] = "Clear history",
        ["No se pudo abrir la copia"] = "Could not open the backup",
        ["La configuración se ha exportado correctamente. Guarda también la contraseña de la copia: no puede recuperarse si se pierde."] = "The configuration was exported successfully. Keep the backup password too: it cannot be recovered if lost.",
        ["Guarda y bloquea todas las bóvedas antes de reemplazar la configuración."] = "Save and lock all vaults before replacing settings.",
        ["Repite la contraseña"] = "Repeat the password",
        ["No se pudo consultar"] = "Could not query",
        ["No se pudo elegir la carpeta"] = "Could not choose the folder",
        ["Selecciona una carpeta"] = "Select a folder",
        ["Limpieza completada"] = "Cleanup completed",
        ["Limpieza parcial"] = "Partial cleanup",
        ["No se pudo configurar el atajo"] = "Could not configure the shortcut",
        ["Atajo no disponible"] = "Shortcut unavailable",
        ["No se pudo cambiar el inicio automático"] = "Could not change automatic startup",
        ["No se pudo iniciar ProtectedApp"] = "Could not start ProtectedApp",
        ["No se pudo iniciar la actualización"] = "Could not start the update",
        ["No se pudo iniciar la desinstalación"] = "Could not start uninstallation",
        ["Guardian es necesario"] = "Guardian is required",
        ["Protección de carpetas"] = "Folder protection",
        ["No se puede cambiar la protección"] = "Could not change protection",
        ["No se pudo aplicar el bloqueo inmediato"] = "Could not apply immediate lock",
        ["Reparación completada"] = "Repair completed",
        ["Reparación incompleta"] = "Repair incomplete",
        ["Autorización requerida"] = "Authorization required",
        ["Contraseña maestra incorrecta"] = "Incorrect master password",
        ["Contraseña maestra incorrecta."] = "Incorrect master password.",
        ["Contraseña incorrecta."] = "Incorrect password.",
        ["Guardian rechazó la autorización."] = "Guardian rejected the authorization.",
        ["Guardian no confirmó el bloqueo."] = "Guardian did not confirm the lock.",
        ["Guardian rechazó la configuración."] = "Guardian rejected the configuration.",
        ["Guardian rechazó el cambio."] = "Guardian rejected the change.",
        ["Guardian no confirmó el cambio de la regla."] = "Guardian did not confirm the rule change.",
        ["Guardian no confirmó el bloqueo selectivo."] = "Guardian did not confirm the selective lock.",
        ["No hay sensor biométrico o PIN configurado en este equipo"] = "No biometric sensor or PIN is configured on this computer",
        ["No se ha configurado un PIN o biometría para tu usuario de Windows"] = "No PIN or biometrics are configured for your Windows account",
        ["El dispositivo biométrico está ocupado"] = "The biometric device is busy",
        ["Versión del motor"] = "Engine version",
        ["Comunicación protegida"] = "Protected communication",
        ["Tarea de recuperación"] = "Recovery task",
        ["Auditoría de seguridad"] = "Security audit",
        ["Recuperación segura"] = "Secure recovery",
        ["Política de protección"] = "Protection policy",
        ["Integración con Explorador"] = "File Explorer integration",
        ["Integridad de bóvedas"] = "Vault integrity",
        ["Recuperación de bóvedas"] = "Vault recovery",
        ["El servicio no está instalado."] = "The service is not installed.",
        ["Instalado y en ejecución como servicio de Windows."] = "Installed and running as a Windows service.",
        ["Está instalado, pero no se encuentra en ejecución."] = "Installed, but not running.",
        ["Guardian no respondió a través del canal local protegido."] = "Guardian did not respond through the protected local channel.",
        ["Guardian confirma que ProtectedApp.Gate está disponible."] = "Guardian confirms that ProtectedApp.Gate is available.",
        ["Guardian confirma que la tarea SYSTEM está habilitada y bien configurada."] = "Guardian confirms that the SYSTEM task is enabled and correctly configured.",
        ["La tarea SYSTEM falta, está deshabilitada o tiene una acción incorrecta."] = "The SYSTEM task is missing, disabled, or has an incorrect action.",
        ["Los binarios protegidos coinciden con su línea base."] = "Protected binaries match their baseline.",
        ["El registro de manipulaciones mantiene una cadena válida."] = "The tamper log has a valid chain.",
        ["No hay incidencias críticas pendientes de recuperación."] = "There are no critical recovery issues pending.",
        ["Esta instalación no tiene una identidad de firma Authenticode verificable."] = "This installation has no verifiable Authenticode signing identity.",
        ["Desactivado por decisión del usuario."] = "Disabled by the user.",
        ["La asociación .pavault y los comandos contextuales de carpetas, bóvedas y unidades están registrados."] = "The .pavault association and folder, vault, and drive context commands are registered.",
        ["Falta la asociación de bóvedas o algún comando contextual. Reinstala ProtectedApp para recuperarlos."] = "The vault association or a context command is missing. Reinstall ProtectedApp to restore it.",
        ["Bóveda cifrada de ProtectedApp"] = "ProtectedApp encrypted vault",
        ["Servicio Guardian"] = "Guardian service",
        ["Integridad de Guardian"] = "Guardian integrity",
        ["Puerta preventiva"] = "Preventive gate",
        ["Identidad de firma"] = "Signing identity",
        ["Inicio con Windows"] = "Start with Windows",
        ["Integridad de las reglas"] = "Rule integrity",
        ["Reglas duplicadas"] = "Duplicate rules",
        ["Unidad virtual Dokany"] = "Dokany virtual drive",
        ["Guardian ha cargado correctamente la política cifrada de este usuario."] = "Guardian has loaded this user's encrypted policy correctly.",
        ["La interfaz se comunica correctamente con Guardian."] = "The interface communicates correctly with Guardian.",
        ["Guardian exige la identidad del certificado de esta instalación."] = "Guardian requires the certificate identity for this installation.",
        ["Configurado en modo silencioso."] = "Configured in background mode.",
        ["No hay rutas protegidas más de una vez."] = "No protected paths are listed more than once.",
        ["Elige qué programas requieren autorización"] = "Choose which programs require authorization",
        ["Tema de la aplicación"] = "App theme",
        ["Pausar protección"] = "Pause protection",
        ["Activar protección del grupo"] = "Enable group protection",
        ["Desactivar protección del grupo"] = "Disable group protection",
        ["Convertir una carpeta en una bóveda cifrada"] = "Convert a folder into an encrypted vault",
        ["Quitar de la lista las bóvedas cuyo archivo y copia de recuperación ya no existen"] = "Remove vaults whose file and recovery backup no longer exist from the list",
        ["Abrir carpeta de recuperación"] = "Open recovery folder",
        ["Descartar carpeta de recuperación"] = "Discard recovery folder",
        ["Guardar y bloquear bóveda"] = "Save and lock vault",
        ["Más acciones de bóveda"] = "More vault actions",
        ["Desbloqueo rápido mediante PIN o huella."] = "Quick unlocking with PIN or fingerprint.",
        ["Webhook HTTPS informativo; no bloquea ni revierte una manipulación."] = "Informational HTTPS webhook; it does not block or reverse tampering.",
        ["Comprobación local de instaladores firmados."] = "Local verification of signed installers.",
        ["Requiere la contraseña maestra."] = "Requires the master password.",
        ["ProtectedApp reúne en un solo lugar la protección de aplicaciones y la creación de bóvedas cifradas para guardar información sensible."] = "ProtectedApp brings application protection and encrypted vault creation together to keep sensitive information safe.",
        ["Las opciones se agrupan por finalidad para que puedas decidir qué proteger sin necesitar conocimientos técnicos."] = "Options are grouped by purpose so you can decide what to protect without technical knowledge.",
        ["Puedes pedir la contraseña maestra antes de abrir una aplicación elegida. También es posible cerrarla automáticamente tras un tiempo o después de un periodo sin actividad; si activas los avisos, ProtectedApp te avisará y permitirá ampliar ese tiempo."] = "You can require the master password before opening an app. It can also close automatically after a time limit or inactivity; if warnings are enabled, ProtectedApp will notify you and let you extend that time.",
        ["Una bóveda es un archivo .pavault que contiene tus archivos cifrados. Al abrirla con tu contraseña se monta como una unidad normal de Windows; al desmontarla, su contenido vuelve a quedar protegido dentro del archivo. Puedes convertir una carpeta existente en una bóveda y comprobar el resultado antes de eliminar la carpeta original."] = "A vault is a .pavault file containing your encrypted files. Opening it with its password mounts it as a normal Windows drive; when unmounted, its content is protected again inside the file. You can convert an existing folder and verify the result before deleting the original.",
        ["Puedes exportar una copia cifrada de la configuración y programar copias de tus bóvedas. Los avisos de bóvedas informan cuando una copia falla, vence o se recupera. Para mantener una bóveda sincronizada con OneDrive u otro servicio similar, sincroniza el archivo .pavault, no una carpeta mientras está abierta."] = "You can export an encrypted configuration backup and schedule vault backups. Vault notifications report when a backup fails, becomes overdue, or is recovered. To sync a vault with OneDrive or a similar service, sync the .pavault file, not an open folder.",
        ["El servicio Guardian comprueba que los componentes de protección estén disponibles y avisa si detecta un problema o una posible manipulación. Las alertas remotas son opcionales y solo envían un aviso a la dirección configurada; no conceden acceso a tus datos."] = "The Guardian service checks that protection components are available and warns about problems or possible tampering. Remote alerts are optional and only send a notification to the configured address; they do not grant access to your data.",
        ["Conserva una copia de seguridad de las bóvedas importantes y no olvides la contraseña maestra: está diseñada para proteger tu información, por lo que ProtectedApp no puede recuperarla por ti."] = "Keep a backup of important vaults and do not forget the master password: it protects your information, so ProtectedApp cannot recover it for you.",
        ["Reaperturas sin contraseña"] = "Password-free reopenings",
        ["Solo mientras continúe abierta"] = "Only while it remains open",
        ["Cierre automático"] = "Automatic closing",
        ["Desactivado"] = "Disabled",
        ["Tras tiempo de uso"] = "After usage time",
        ["Tras inactividad"] = "After inactivity",
        ["Tiempo"] = "Time",
        ["Selecciona un modo"] = "Select a mode",
        ["5 minutos"] = "5 minutes",
        ["15 minutos"] = "15 minutes",
        ["1 hora"] = "1 hour",
        ["Tiempo personalizado…"] = "Custom time…",
        ["Minutos personalizados"] = "Custom minutes",
        ["Máximo: 10.080 min"] = "Maximum: 10,080 min",
        ["Horario"] = "Schedule",
        ["Protección permanente"] = "Always protect",
        ["Proteger solo durante el horario"] = "Protect only during schedule",
        ["Proteger durante el horario y bloquear fuera"] = "Protect during schedule and lock outside it",
        ["Desde"] = "From",
        ["Hasta"] = "To",
        ["Días activos"] = "Active days",
        ["El cierre por inactividad cuenta desde la última interacción de teclado o ratón mientras la aplicación está en primer plano. Solo puede usarse un modo de cierre a la vez."] = "Inactivity closing is measured from the last keyboard or mouse interaction while the app is in the foreground. Only one closing mode can be used at a time.",
        ["Si ambas horas coinciden, el horario cubre las 24 horas de los días elegidos. Los intervalos nocturnos pueden terminar al día siguiente."] = "If both times are the same, the schedule covers all 24 hours of the selected days. Overnight periods can end the next day.",
        ["Brave podrá volver a abrirse sin contraseña."] = "Brave can be opened again without a password.",
        ["Se revocará únicamente el acceso de Brave y se cerrarán sus procesos abiertos. El trabajo no guardado podría perderse."] = "Only Brave access will be revoked and its open processes will be closed. Unsaved work may be lost.",
        ["Ejecución interceptada por Guardian (SYSTEM)"] = "Launch intercepted by Guardian (SYSTEM)",
        ["Acceso autorizado por Guardian; aplicación iniciada"] = "Access authorized by Guardian; application started",
        ["Sincronización de política pendiente: Guardian se reinició o la autorización dejó de ser válida; se reintentará automáticamente"] = "Policy synchronization pending: Guardian restarted or authorization is no longer valid; it will be retried automatically",
        ["Política sincronizada automáticamente después de recuperar la conexión"] = "Policy synchronized automatically after connection recovery",
        ["Modo oscuro activo. Cambiar a modo claro"] = "Dark mode is active. Switch to light mode",
        ["Modo claro activo. Cambiar a modo oscuro"] = "Light mode is active. Switch to dark mode",
        ["Usar modo claro"] = "Use light mode",
        ["Usar modo oscuro"] = "Use dark mode",
        ["Seguir el tema de Windows"] = "Follow the Windows theme",
        ["Cerrar"] = "Close",
        ["Minimizar"] = "Minimize",
        ["Minimizar a la barra de tareas"] = "Minimize to taskbar",
        ["Ocultar ProtectedApp"] = "Hide ProtectedApp",
        ["Cancelar"] = "Cancel",
        ["Desactivar"] = "Disable",
        ["Acerca de ProtectedApp"] = "About ProtectedApp",
        ["Bloquear ahora"] = "Lock now",
        ["Revocar accesos y bloquear ahora"] = "Revoke access and lock now",
        ["Revocar accesos y cerrar las aplicaciones protegidas"] = "Revoke access and close protected applications",
        ["Editar archivos"] = "Edit files",
        ["Consultar es recomendable cuando no necesitas modificar archivos."] = "Viewing is recommended when you do not need to modify files.",
        ["Firmar el contenido con HMAC-SHA256"] = "Sign content with HMAC-SHA256",
        ["Integridad de carpetas"] = "Folder integrity",
        ["Integridad de bóvedas"] = "Vault integrity",
        ["Unidades virtuales montadas"] = "Mounted virtual drives",
        ["Recuperación de bóvedas"] = "Vault recovery",
        ["No hay carpetas protegidas que comprobar."] = "There are no protected folders to check.",
        ["No hay bóvedas registradas que comprobar."] = "There are no registered vaults to check.",
        ["No hay bóvedas abiertas como unidad virtual."] = "No vaults are open as a virtual drive.",
        ["No hay diarios ni copias de recuperación pendientes."] = "There are no pending journals or recovery backups.",
        ["Desbloquear"] = "Unlock",
        ["Contraseña"] = "Password",
        ["Mostrar contraseña"] = "Show password",
        ["Ocultar contraseña"] = "Hide password",
        ["Bloq Mayús está activado"] = "Caps Lock is on",
        ["Introduce la contraseña para continuar"] = "Enter the password to continue",
        ["Desbloquear con Windows Hello (PIN / Biometría)"] = "Unlock with Windows Hello (PIN / Biometrics)",
        ["Autorización requerida"] = "Authorization required",
        ["Ejecución bloqueada"] = "Launch blocked",
        ["No se pudo iniciar"] = "Could not start",
        ["Entendido"] = "OK",
        ["Ahora no"] = "Not now",
        ["Autoriza el desbloqueo de"] = "Authorize unlocking",
        ["Autorización no concedida."] = "Authorization was not granted."
        ,["Aceptar"] = "OK"
        ,["Guardian no disponible"] = "Guardian unavailable"
        ,["Servicio no disponible"] = "Service unavailable"
        ,["No se pudo abrir"] = "Could not open"
        ,["No se pudo guardar"] = "Could not save"
        ,["No se pudo bloquear"] = "Could not lock"
        ,["No se pudo recuperar"] = "Could not recover"
        ,["No se pudo restaurar"] = "Could not restore"
        ,["No se pudo eliminar"] = "Could not delete"
        ,["No se pudo cambiar la protección"] = "Could not change protection"
        ,["No se pudo cambiar la contraseña"] = "Could not change password"
        ,["No se pudo crear"] = "Could not create"
        ,["No se pudo convertir"] = "Could not convert"
        ,["No se pudo abrir la carpeta"] = "Could not open folder"
        ,["No se pudo abrir el Explorador"] = "Could not open File Explorer"
        ,["No se pudo abrir la copia"] = "Could not open backup"
        ,["No se pudo crear la copia"] = "Could not create backup"
        ,["Copia creada"] = "Backup created"
        ,["Copia restaurada"] = "Backup restored"
        ,["Bóveda creada; carpeta original conservada"] = "Vault created; original folder kept"
        ,["Bóveda duplicada"] = "Duplicate vault"
        ,["Bóveda abierta"] = "Vault is open"
        ,["Bóveda recuperada"] = "Vault recovered"
        ,["Bóveda no encontrada"] = "Vault not found"
        ,["Bóveda no disponible"] = "Vault unavailable"
        ,["Integridad correcta"] = "Integrity verified"
        ,["Integridad no confirmada"] = "Integrity not confirmed"
        ,["Recuperación completada"] = "Recovery completed"
        ,["Recuperación pendiente"] = "Recovery pending"
        ,["Actualización rechazada"] = "Update rejected"
        ,["No se pudo actualizar"] = "Could not update"
        ,["No se pudo seleccionar el instalador"] = "Could not select installer"
        ,["No hay eventos"] = "No events"
        ,["No hay referencias ausentes"] = "No missing references"
        ,["Sin destino configurado"] = "No destination configured"
        ,["Sin cambios"] = "No changes"
        ,["Sin aplicaciones"] = "No applications"
        ,["Ya está protegido"] = "Already protected"
        ,["Archivo no compatible"] = "Unsupported file"
        ,["Aplicación no permitida"] = "Application not allowed"
        ,["Acción no disponible"] = "Action unavailable"
        ,["Documento no disponible"] = "Document unavailable"
        ,["No se pudo abrir el documento"] = "Could not open document"
        ,["Activo · reglas aplicadas por Guardian como SYSTEM"] = "Active · rules applied by Guardian as SYSTEM"
        ,["Activo · pendiente de actualizar el motor de protección"] = "Active · protection engine update pending"
        ,["Instalado pero detenido · pendiente de recuperación"] = "Installed but stopped · recovery pending"
        ,["No instalado · protección no disponible"] = "Not installed · protection unavailable"
        ,["Servicio activo"] = "Service active"
        ,["Reparar servicio"] = "Repair service"
        ,["Instalar servicio"] = "Install service"
        ,["ProtectedApp — protección activa"] = "ProtectedApp — protection active"
        ,["Bóveda sin nombre"] = "Unnamed vault"
        ,["Abrir ProtectedApp"] = "Open ProtectedApp"
        ,["Desmontar:"] = "Unmount:"
        ,["Abrir bóveda:"] = "Open vault:"
        ,["Bloquear y ocultar"] = "Lock and hide"
        ,["Correcto"] = "Healthy"
        ,["Revisar"] = "Review"
        ,["Bloqueo"] = "Lock"
        ,["Acceso"] = "Access"
        ,["Aviso"] = "Warning"
        ,["Sistema"] = "System"
        ,["Protegiendo…"] = "Securing…"
        ,["Consulta segura"] = "Read-only view"
        ,["Cambios pendientes"] = "Pending changes"
        ,["Recuperación disponible"] = "Recovery available"
        ,["Copia dañada"] = "Backup damaged"
        ,["Cambios recuperables"] = "Recoverable changes"
        ,["Cerrada"] = "Locked"
        ,["Vacía"] = "Empty"
        ,["Copias programadas sin configurar"] = "Scheduled backups not configured"
        ,["⚠ Copia programada pendiente"] = "⚠ Scheduled backup pending"
        ,["⚠ Copia programada vencida"] = "⚠ Scheduled backup overdue"
        ,["⚠ Error al crear la copia programada"] = "⚠ Scheduled backup failed"
        ,["Revisión manual necesaria"] = "Manual review required"
        ,["Apertura incompleta"] = "Incomplete opening"
        ,["No asociada"] = "Not linked"
        ,["Falta el contenedor"] = "Container missing"
        ,["Lista para recuperar"] = "Ready to recover"
        ,["No se indicó la contraseña de la bóveda."] = "No vault password was provided."
        ,["La bóveda contiene demasiados elementos."] = "The vault contains too many items."
        ,["El contenido de la bóveda supera 1 GB."] = "Vault contents exceed 1 GB."
        ,["Los bloques cifrados superan el límite admitido."] = "Encrypted blocks exceed the supported limit."
        ,["El índice de la bóveda es demasiado grande."] = "The vault index is too large."
        ,["La verificación del contenedor nuevo devolvió otra bóveda."] = "New container verification returned a different vault."
        ,["La clave de datos no es válida."] = "The data key is invalid."
        ,["La ruta de la bóveda no es válida."] = "The vault path is invalid."
        ,["La longitud de un archivo no es válida."] = "A file length is invalid."
        ,["El archivo no existe dentro de la bóveda."] = "The file does not exist inside the vault."
        ,["La prueba de escritura virtual devolvió datos distintos."] = "The virtual write test returned different data."
        ,["La comprobación de integridad no validó el contenido esperado."] = "Integrity checking did not validate the expected content."
        ,["La nueva contraseña no superó la verificación final."] = "The new password did not pass final verification."
        ,["No se pudo leer el índice PAVLT003."] = "Could not read the PAVLT003 index."
        ,["La contraseña es incorrecta o la bóveda ha sido modificada."] = "The password is incorrect or the vault has been modified."
        ,["La bóveda contiene demasiados bloques."] = "The vault contains too many blocks."
        ,["El archivo cambió mientras se estaba cifrando."] = "The file changed while it was being encrypted."
        ,["La longitud del bloque no es válida."] = "The block length is invalid."
        ,["Un bloque de la bóveda ha sido modificado."] = "A vault block has been modified."
        ,["La actualización del índice no superó la verificación."] = "The index update did not pass verification."
        ,["La derivación de clave PAVLT003 no es compatible."] = "PAVLT003 key derivation is not supported."
        ,["La cabecera PAVLT003 no es válida."] = "The PAVLT003 header is invalid."
        ,["La cabecera PAVLT003 tiene un tamaño inesperado."] = "The PAVLT003 header has an unexpected size."
        ,["El índice no contiene metadatos de bóveda válidos."] = "The index contains no valid vault metadata."
        ,["El índice contiene demasiados elementos."] = "The index contains too many items."
        ,["El índice contiene rutas duplicadas."] = "The index contains duplicate paths."
        ,["Una entrada del índice no es válida."] = "An index entry is invalid."
        ,["Un bloque del índice no es válido."] = "An index block is invalid."
        ,["La longitud de un bloque sin compresión no es válida."] = "An uncompressed block length is invalid."
        ,["La zona de datos cifrados no coincide con el índice."] = "The encrypted data area does not match the index."
        ,["Las bóvedas no admiten enlaces ni puntos de montaje."] = "Vaults do not support links or mount points."
        ,["El índice contiene una ruta no válida."] = "The index contains an invalid path."
        ,["El índice contiene una ruta no segura."] = "The index contains an unsafe path."
        ,["Una ruta de la bóveda es demasiado larga."] = "A vault path is too long."
        ,["El índice intenta salir de la carpeta de trabajo."] = "The index attempts to leave the working folder."
        ,["El material de clave PAVLT003 no es válido."] = "PAVLT003 key material is invalid."
        ,["El contenedor está truncado."] = "The container is truncated."
        ,["La necesitarás para abrir ProtectedApp y cambiar ajustes."] = "You will need it to open ProtectedApp and change settings."
        ,["Autorizar restauración"] = "Authorize restoration"
        ,["Intento de restauración rechazado: contraseña incorrecta o copia no válida"] = "Restore attempt rejected: incorrect password or invalid backup"
        ,["Contraseña incorrecta al intentar desbloquear la aplicación"] = "Incorrect password while trying to unlock the application"
        ,["Acceso autorizado; aplicación iniciada"] = "Access authorized; application started"
        ,["Protección administrada"] = "Managed protection"
        ,["ProtectedApp aplica la protección exclusivamente mediante Guardian. No se puede pausar desde la interfaz."] = "ProtectedApp applies protection exclusively through Guardian. It cannot be paused from the interface."
        ,["Reinstala ProtectedApp para recuperar la documentación legal."] = "Reinstall ProtectedApp to recover the legal documentation."
        ,["Doble clic en bóvedas configurado para consulta segura"] = "Double-click vaults set to safe viewing"
        ,["Doble clic en bóvedas configurado para editar"] = "Double-click vaults set to editing"
        ,["Letra de unidad de bóvedas configurada automáticamente"] = "Vault drive letter set automatically"
        ,["Avisos de copias de bóvedas activados"] = "Vault backup notifications enabled"
        ,["Avisos de copias de bóvedas silenciados"] = "Vault backup notifications muted"
        ,["Avisos previos de cierre automático activados"] = "Automatic closing warnings enabled"
        ,["Avisos previos de cierre automático desactivados"] = "Automatic closing warnings disabled"
        ,["Guardian rechazó la solicitud."] = "Guardian rejected the request."
        ,["Copia programada pendiente: todavía no existe ninguna versión"] = "Scheduled backup pending: no version exists yet"
        ,["Copia programada vencida: la última versión superó la frecuencia configurada"] = "Scheduled backup overdue: the latest version exceeded the configured frequency"
        ,["Protección de bóvedas"] = "Vault protection"
        ,["Mayús"] = "Shift"
        ,["Atajo de bloqueo inmediato ignorado: Guardian no está disponible"] = "Immediate lock shortcut ignored: Guardian is unavailable"
        ,["No se pudieron cerrar todas las bóvedas durante la respuesta antimanipulación; se conservó su carpeta de trabajo"] = "Not all vaults could be closed during the anti-tamper response; their working folder was preserved"
        ,["Protección desactivada: Guardian no admite carpetas sincronizadas o redirigidas"] = "Protection disabled: Guardian does not support synchronized or redirected folders"
        ,["Cierre automático ampliado"] = "Automatic closing extended"
        ,["Ejecución interceptada por Guardian (SYSTEM)"] = "Launch intercepted by Guardian (SYSTEM)"
        ,["Manipulación detectada"] = "Tampering detected"
        ,["Copia programada de bóveda creada"] = "Scheduled vault backup created"
        ,["Bóveda restaurada desde la copia anterior durante la importación"] = "Vault restored from the previous backup during import"
        ,["Bóveda cifrada importada"] = "Encrypted vault imported"
        ,["No se pudo desmontar automáticamente la unidad virtual"] = "Could not automatically unmount the virtual drive"
        ,["No se pudo aplicar el bloqueo automático"] = "Could not apply automatic lock"
        ,["Consulta segura cerrada y bóveda protegida"] = "Safe viewing closed and vault protected"
        ,["Cambios guardados y bóveda protegida"] = "Changes saved and vault protected"
        ,["No se pudo cerrar la sesión de consulta segura."] = "Could not close the safe viewing session."
        ,["La bóveda sigue abierta para editar para evitar perder cambios."] = "The vault remains open for editing to avoid losing changes."
        ,["Bóveda retirada de la lista; el contenedor cifrado se conservó"] = "Vault removed from the list; the encrypted container was kept"
        ,["Comprobar versión"] = "Check version"
        ,["Eliminar versión"] = "Delete version"
        ,["Versión de copia programada eliminada"] = "Scheduled backup version deleted"
        ,["Contraseña incorrecta o versión no válida."] = "Incorrect password or invalid version."
        ,["Versión comprobada"] = "Version checked"
        ,["Versión restaurada"] = "Version restored"
        ,["La versión se conservó sin cambios."] = "The version was kept unchanged."
        ,["La versión se verificó y se restauró correctamente."] = "The version was verified and restored successfully."
        ,["Los datos de la bóveda o la contraseña no son válidos."] = "Vault data or password is invalid."
        ,["Los datos de la bóveda, la carpeta o la contraseña no son válidos."] = "Vault data, folder, or password is invalid."
        ,["La carpeta de origen ya no existe."] = "The source folder no longer exists."
        ,["Ya existe un archivo en la ubicación elegida para la bóveda."] = "A file already exists at the selected vault location."
        ,["La bóveda no puede guardarse dentro de la carpeta que se va a cifrar."] = "The vault cannot be saved inside the folder being encrypted."
        ,["La verificación final devolvió una bóveda distinta."] = "Final verification returned a different vault."
        ,["No se pudo crear la primera copia programada."] = "Could not create the first scheduled backup."
        ,["No se pudo crear la segunda copia programada."] = "Could not create the second scheduled backup."
        ,["Las copias programadas no conservaron las dos versiones esperadas."] = "Scheduled backups did not retain the expected two versions."
        ,["No se pudo enumerar el historial de copias programadas."] = "Could not enumerate scheduled backup history."
        ,["La versión programada no superó la comprobación estructural."] = "The scheduled version did not pass structural verification."
        ,["La versión programada no superó la validación autenticada."] = "The scheduled version did not pass authenticated validation."
        ,["La limpieza de versiones programadas no conservó solo la versión reciente."] = "Scheduled version cleanup did not retain only the latest version."
        ,["No quedó una versión recuperable tras la limpieza."] = "No recoverable version remained after cleanup."
        ,["La restauración de una versión programada no preservó el contenedor actual."] = "Restoring a scheduled version did not preserve the current container."
        ,["La bóveda debe estar cerrada y tener un contenedor disponible."] = "The vault must be closed and have an available container."
        ,["La carpeta de copias no puede ser la misma bóveda ni contenerla."] = "The backup folder cannot be the vault itself or contain it."
        ,["La copia creada no superó la verificación estructural."] = "The created backup did not pass structural verification."
        ,["La copia temporal pertenece a otra bóveda."] = "The temporary backup belongs to a different vault."
        ,["El contenedor restaurado no superó la verificación final."] = "The restored container did not pass final verification."
        ,["La bóveda no tiene un contenedor cifrado asociado."] = "The vault has no associated encrypted container."
        ,["La bóveda ya tiene una sesión abierta."] = "The vault already has an open session."
        ,["El montaje virtual necesita PAVLT003. Abre esta bóveda para editar y bloquéala una vez para migrarla."] = "Virtual mounting requires PAVLT003. Open this vault for editing and lock it once to migrate it."
        ,["La edición virtual necesita PAVLT003. Abre y bloquea esta bóveda una vez para migrarla."] = "Virtual editing requires PAVLT003. Open and lock this vault once to migrate it."
        ,["No hay ninguna letra de unidad disponible para montar la bóveda."] = "No drive letter is available to mount the vault."
        ,["Dokany no pudo iniciar la unidad virtual."] = "Dokany could not start the virtual drive."
        ,["Dokany no pudo iniciar la unidad virtual editable."] = "Dokany could not start the editable virtual drive."
        ,["Ya existe un montaje virtual para esta bóveda."] = "A virtual mount already exists for this vault."
        ,["Ya existe una operación abierta para esta bóveda."] = "An operation is already in progress for this vault."
        ,["No existe una sesión abierta para esta bóveda."] = "There is no open session for this vault."
        ,["La carpeta de trabajo ya no existe."] = "The working folder no longer exists."
        ,["El contenedor pertenece a otra bóveda."] = "The container belongs to a different vault."
        ,["La copia verificada pertenece a otra bóveda."] = "The verified backup belongs to a different vault."
        ,["La copia es un enlace o punto de análisis y no se eliminará automáticamente."] = "The backup is a link or reparse point and will not be removed automatically."
        ,["La ruta no pertenece al área privada de recuperación."] = "The path does not belong to the private recovery area."
        ,["No se puede inspeccionar la carpeta."] = "The folder cannot be inspected."
        ,["La carpeta contiene enlaces o puntos de montaje y no puede eliminarse automáticamente."] = "The folder contains links or mount points and cannot be removed automatically."
        ,["La bóveda está abierta y no se puede descartar su trabajo."] = "The vault is open and its work cannot be discarded."
        ,["No se pudo identificar al usuario propietario de la bóveda."] = "Could not identify the vault owner."
        ,["La bóveda no existe o no se indicó contraseña."] = "The vault does not exist or no password was provided."
        ,["El tamaño de la bóveda no es válido."] = "The vault size is invalid."
        ,["La cabecera de la bóveda está incompleta."] = "The vault header is incomplete."
        ,["El formato de la bóveda no es compatible."] = "The vault format is not supported."
        ,["La derivación de clave no es compatible."] = "Key derivation is not supported."
        ,["La longitud cifrada no es válida."] = "The encrypted length is invalid."
        ,["La bóveda supera el tamaño máximo admitido en esta versión."] = "The vault exceeds the maximum size supported by this version."
        ,["El contenido de la bóveda es demasiado grande."] = "Vault contents are too large."
        ,["La bóveda cifrada es demasiado grande."] = "The encrypted vault is too large."
        ,["La bóveda contiene una entrada desconocida."] = "The vault contains an unknown entry."
        ,["El contenido expandido es demasiado grande."] = "Expanded contents are too large."
        ,["La bóveda no contiene metadatos."] = "The vault contains no metadata."
        ,["Los metadatos de la bóveda no son válidos."] = "Vault metadata is invalid."
        ,["No se pudieron leer los metadatos de la bóveda."] = "Could not read vault metadata."
        ,["Los metadatos de la bóveda están incompletos."] = "Vault metadata is incomplete."
        ,["El archivo está truncado."] = "The file is truncated."
        ,["El servicio no está instalado."] = "The service is not installed."
        ,["Instalado y en ejecución como servicio de Windows."] = "Installed and running as a Windows service."
        ,["Está instalado, pero no se encuentra en ejecución."] = "It is installed, but is not running."
        ,["Versión del motor"] = "Engine version"
        ,["Comunicación protegida"] = "Protected communication"
        ,["Guardian no respondió a través del canal local protegido."] = "Guardian did not respond through the protected local channel."
        ,["Puerta preventiva"] = "Preventive gate"
        ,["Guardian confirma que ProtectedApp.Gate está disponible."] = "Guardian confirms that ProtectedApp.Gate is available."
        ,["La versión instalada de Guardian no admite esta comprobación protegida."] = "The installed Guardian version does not support this protected check."
        ,["Tarea de recuperación"] = "Recovery task"
        ,["Guardian confirma que la tarea SYSTEM está habilitada y bien configurada."] = "Guardian confirms that the SYSTEM task is enabled and correctly configured."
        ,["La tarea SYSTEM falta, está deshabilitada o tiene una acción incorrecta."] = "The SYSTEM task is missing, disabled, or has an incorrect action."
        ,["La cuenta de usuario no puede consultarla; se necesita Guardian actualizado."] = "The user account cannot query it; an updated Guardian is required."
        ,["Los binarios protegidos coinciden con su línea base."] = "Protected binaries match their baseline."
        ,["Guardian no respondió para comprobar sus binarios."] = "Guardian did not respond to check its binaries."
        ,["Auditoría de seguridad"] = "Security audit"
        ,["El registro de manipulaciones mantiene una cadena válida."] = "The tamper log has a valid chain."
        ,["El registro protegido presenta una discontinuidad."] = "The protected log has a discontinuity."
        ,["Guardian no respondió para comprobar el registro protegido."] = "Guardian did not respond to check the protected log."
        ,["Recuperación segura"] = "Secure recovery"
        ,["Guardian mantiene las puertas preventivas activas hasta completar una reparación."] = "Guardian keeps preventive gates active until repair is complete."
        ,["No hay incidencias críticas pendientes de recuperación."] = "There are no critical recovery issues pending."
        ,["No se pudo consultar el estado de recuperación."] = "Could not query recovery status."
        ,["Identidad de firma"] = "Signing identity"
        ,["Guardian exige la identidad del certificado de esta instalación."] = "Guardian requires this installation's certificate identity."
        ,["Esta instalación no tiene una identidad de firma Authenticode verificable."] = "This installation has no verifiable Authenticode signing identity."
        ,["Guardian no respondió para comprobar la identidad de firma."] = "Guardian did not respond to check the signing identity."
        ,["No hay rutas protegidas más de una vez."] = "No protected paths appear more than once."
        ,["Política de protección"] = "Protection policy"
        ,["Guardian ha cargado correctamente la política cifrada de este usuario."] = "Guardian loaded this user's encrypted policy successfully."
        ,["Guardian no tiene una política válida para este usuario."] = "Guardian has no valid policy for this user."
        ,["Inicio con Windows"] = "Start with Windows"
        ,["Desactivado por decisión del usuario."] = "Disabled by user choice."
        ,["Integración con Explorador"] = "File Explorer integration"
        ,["La asociación .pavault y los comandos contextuales de carpetas, bóvedas y unidades están registrados."] = "The .pavault association and folder, vault, and drive context commands are registered."
        ,["Falta la asociación de bóvedas o algún comando contextual. Reinstala ProtectedApp para recuperarlos."] = "The vault association or a context command is missing. Reinstall ProtectedApp to recover them."
        ,["El runtime se cargó, pero el controlador de unidades virtuales no responde. Reinicia Windows o reinstala ProtectedApp."] = "The runtime loaded, but the virtual-drive driver does not respond. Restart Windows or reinstall ProtectedApp."
        ,["El componente necesario para montar bóvedas no está disponible. Reinstala ProtectedApp para instalar Dokany."] = "The component required to mount vaults is unavailable. Reinstall ProtectedApp to install Dokany."
        ,["Integridad de bóvedas"] = "Vault integrity"
        ,["Unidades virtuales montadas"] = "Mounted virtual drives"
        ,["No hay bóvedas abiertas como unidad virtual."] = "No vaults are open as a virtual drive."
        ,["Recuperación de bóvedas"] = "Vault recovery"
        ,["No hay diarios ni copias de recuperación pendientes."] = "There are no pending journals or recovery backups."
        ,["Ejecución bloqueada fuera del horario permitido"] = "Launch blocked outside the permitted schedule"
        ,["Ejecución interceptada antes de mostrar su ventana"] = "Launch intercepted before its window was shown"
        ,["No se pudo cerrar el proceso; quizá requiere permisos de administrador"] = "Could not close the process; administrator permission may be required"
        ,["Bóvedas"] = "Vaults"
        ,["Elige qué programas requieren autorización"] = "Choose which programs require authorization"
        ,["Tema de la aplicación"] = "App theme"
        ,["Pausar protección"] = "Pause protection"
        ,["Activar protección del grupo"] = "Enable group protection"
        ,["Desactivar protección del grupo"] = "Disable group protection"
        ,["Editar"] = "Edit"
        ,["Editar protección"] = "Edit protection"
        ,["Bloquear esta aplicación"] = "Lock this application"
        ,["Bloquear aplicación"] = "Lock application"
        ,["Eliminar"] = "Delete"
        ,["Eliminar protección"] = "Remove protection"
        ,["Contenedores cifrados con contraseña propia"] = "Encrypted containers with their own password"
        ,["Consulta archivos sin crear una copia completa en el disco o abre la bóveda para editar y guardar cambios."] = "View files without creating a full copy on disk, or open the vault to edit and save changes."
        ,["Convertir carpeta"] = "Convert folder"
        ,["Convertir una carpeta en una bóveda cifrada"] = "Convert a folder into an encrypted vault"
        ,["Limpiar ausentes"] = "Clean missing"
        ,["Quitar de la lista las bóvedas cuyo archivo y copia de recuperación ya no existen"] = "Remove from the list vaults whose file and recovery backup no longer exist"
        ,["Recuperación pendiente"] = "Recovery pending"
        ,["Abrir carpeta de trabajo"] = "Open working folder"
        ,["Abrir carpeta de recuperación"] = "Open recovery folder"
        ,["Descartar trabajo"] = "Discard work"
        ,["Descartar carpeta de recuperación"] = "Discard recovery folder"
        ,["UBICACIÓN DEL ARCHIVO"] = "FILE LOCATION"
        ,["Abrir bóveda"] = "Open vault"
        ,["Guardar cambios y bloquear"] = "Save changes and lock"
        ,["Guardar y bloquear bóveda"] = "Save and lock vault"
        ,["Más acciones de bóveda"] = "More vault actions"
        ,["Copia de recuperación"] = "Recovery backup"
        ,["Editar bóveda"] = "Edit vault"
        ,["Quitar bóveda"] = "Remove vault"
        ,["No hay bóvedas cifradas"] = "No encrypted vaults"
        ,["Mostrar eventos de las últimas 24 horas"] = "Show events from the last 24 hours"
        ,["Filtrar eventos de las últimas 24 horas"] = "Filter events from the last 24 hours"
        ,["Mostrar contraseñas fallidas"] = "Show failed passwords"
        ,["Filtrar contraseñas fallidas"] = "Filter failed passwords"
        ,["Aún no hay actividad"] = "There is no activity yet"
        ,["Diagnóstico todavía no ejecutado"] = "Diagnostics not run yet"
        ,["Comprueba los componentes de protección sin modificar el sistema."] = "Check protection components without modifying the system."
        ,["Reparar protección"] = "Repair protection"
        ,["Desbloqueo rápido mediante PIN o huella."] = "Quick unlock with PIN or fingerprint."
        ,["Sin configurar."] = "Not configured."
        ,["Modo viaje"] = "Travel mode"
        ,["Activar…"] = "Enable…"
        ,["Webhook HTTPS informativo; no bloquea ni revierte una manipulación."] = "Informational HTTPS webhook; it does not block or reverse tampering."
        ,["Comprobación local de instaladores firmados."] = "Local check of signed installers."
        ,["Requiere la contraseña maestra."] = "Requires the master password."
        ,["Protección local gratuita para aplicaciones y bóvedas"] = "Free local protection for applications and vaults"
        ,["INFORMACIÓN"] = "INFORMATION"
        ,["Guardian aplica las reglas protegidas como SYSTEM. Las bóvedas cifradas se montan mediante Dokany y protegen el contenido mediante cifrado autenticado."] = "Guardian applies protected rules as SYSTEM. Encrypted vaults are mounted through Dokany and protect content with authenticated encryption."
        ,["© 2026 Valvik. ProtectedApp complementa la seguridad de Windows y no sustituye las políticas empresariales."] = "© 2026 Valvik. ProtectedApp complements Windows security and does not replace enterprise policies."
        ,["Copias y recuperación"] = "Backups and recovery"
        ,["El contenedor principal no es válido, pero su copia cifrada anterior sí. ProtectedApp puede restaurarla y conservar el archivo dañado antes de importar la bóveda."] = "The primary container is invalid, but its previous encrypted backup is valid. ProtectedApp can restore it and preserve the damaged file before importing the vault."
        ,["Contraseña incorrecta al recuperar la bóveda"] = "Incorrect password while recovering the vault"
        ,["Esta acción elimina permanentemente la carpeta de trabajo sin incorporar sus cambios al contenedor cifrado."] = "This action permanently removes the working folder without adding its changes to the encrypted container."
        ,["Recupera o descarta primero la carpeta de trabajo conservada. Quitar ahora la bóveda impediría guardarla desde ProtectedApp."] = "Recover or discard the preserved working folder first. Removing the vault now would prevent saving it from ProtectedApp."
        ,["Todas las bóvedas de la lista tienen su contenedor principal, una copia recuperable o trabajo pendiente que requiere atención."] = "All vaults in the list have their primary container, a recoverable backup, or pending work requiring attention."
        ,["Guarda y bloquea la bóveda y resuelve cualquier recuperación pendiente antes de eliminarla."] = "Save and lock the vault and resolve any pending recovery before deleting it."
        ,["Bóveda eliminada permanentemente; las copias cifradas se conservaron"] = "Vault permanently deleted; encrypted backups were retained"
        ,["La nueva contraseña debe tener al menos 8 caracteres."] = "The new password must be at least 8 characters."
        ,["Contraseña incorrecta o copia no válida."] = "Incorrect password or invalid backup."
        ,["Confirmar restauración"] = "Confirm restoration"
        ,["La copia se verificó y se restauró como contenedor principal."] = "The backup was verified and restored as the primary container."
        ,["Introduce la contraseña de la bóveda para validar la versión seleccionada"] = "Enter the vault password to validate the selected version"
        ,["Contraseña incorrecta o cabecera no válida."] = "Incorrect password or invalid header."
        ,["No hay una copia cifrada anterior válida para recuperarla automáticamente."] = "There is no valid previous encrypted backup to recover automatically."
        ,["El contenedor principal no superó la comprobación completa de integridad, pero su copia anterior es válida"] = "The primary container did not pass the full integrity check, but its previous backup is valid"
        ,["Bóveda"] = "Vault"
        ,["Elige cómo quieres abrir tus archivos. Consultar permite verlos sin crear una copia completa en el disco. Editar permite modificar y guardar cambios al bloquear la bóveda."] = "Choose how to open your files. Viewing lets you see them without creating a full copy on disk. Editing lets you modify and save changes when locking the vault."
        ,["Contraseña incorrecta al abrir la bóveda"] = "Incorrect password while opening the vault"
        ,["No se pudo abrir la bóveda para editar."] = "Could not open the vault for editing."
        ,["La copia se restauró, pero no se pudo abrir la bóveda."] = "The backup was restored, but the vault could not be opened."
        ,["Referencias duplicadas de la bóveda corregidas tras validar el contenedor"] = "Duplicate vault references corrected after validating the container"
        ,["Cambios recuperados de una sesión interrumpida; guarda y bloquea la bóveda para consolidarlos"] = "Changes recovered from an interrupted session; save and lock the vault to consolidate them"
        ,["Bóveda abierta para editar; guarda y bloquea antes de cerrar"] = "Vault open for editing; save and lock before closing"
        ,["Una bóveda no pudo cerrarse de forma segura. Revisa sus archivos de trabajo antes de continuar."] = "A vault could not be closed safely. Review its working files before continuing."
        ,["Aviso de recuperación de bóveda revisado por el usuario"] = "Vault recovery notice reviewed by the user"
        ,["Guardian rechazó la política de protección. Revisa las carpetas protegidas y vuelve a intentarlo."] = "Guardian rejected the protection policy. Review protected folders and try again."
        ,["El servicio arrancó, pero Guardian no confirmó una política protegida. La protección permanece deshabilitada hasta que se repare el servicio."] = "The service started, but Guardian did not confirm a protected policy. Protection remains disabled until the service is repaired."
        ,["Windows no confirmó la eliminación del servicio. Comprueba el aviso de UAC y vuelve a intentarlo."] = "Windows did not confirm service removal. Check the UAC prompt and try again."
        ,["Bloqueo automático del panel desactivado"] = "Automatic panel lock disabled"
        ,["día"] = "day"
        ,["Autorización de administración no válida"] = "Invalid administration authorization"
        ,["ProtectedApp no permite proteger sus propios componentes ni procesos esenciales de Windows porque podría dejar la sesión inutilizable."] = "ProtectedApp does not allow protecting its own components or essential Windows processes because it could make the session unusable."
        ,["Se revocarán los periodos de confianza, se cerrarán las aplicaciones protegidas abiertas, se bloquearán las carpetas y se guardarán y cerrarán las bóvedas. El trabajo no guardado en otras aplicaciones podría perderse."] = "Trusted periods will be revoked, open protected applications closed, folders locked, and vaults saved and closed. Unsaved work in other applications may be lost."
        ,["Se revocarán los periodos de confianza. Las aplicaciones protegidas recibirán primero una solicitud de cierre normal y tendrán hasta 5 segundos para cerrarse; las que sigan abiertas se cerrarán forzosamente. Se bloquearán las carpetas y se guardarán y cerrarán las bóvedas."] = "Trusted periods will be revoked. Protected applications will first receive a normal close request and have up to 5 seconds to close; those still open will be forcibly closed. Folders will be locked and vaults saved and closed."
        ,["Se cerrarán las aplicaciones protegidas, se bloquearán las carpetas, se guardarán y desmontarán las bóvedas abiertas y, finalmente, se bloqueará Windows."] = "Protected applications will be closed, folders locked, open vaults saved and unmounted, and finally Windows will be locked."
        ,["Las aplicaciones protegidas recibirán primero una solicitud de cierre normal y tendrán hasta 5 segundos para cerrarse; las que sigan abiertas se cerrarán forzosamente. Después se bloquearán las carpetas, se guardarán y desmontarán las bóvedas abiertas y se bloqueará Windows."] = "Protected applications will first receive a normal close request and have up to 5 seconds to close; those still open will be forcibly closed. Folders will then be locked, open vaults saved and unmounted, and Windows locked."
        ,["Introduce la contraseña de la copia."] = "Enter the backup password."
        ,["Miércoles"] = "Wednesday"
        ,["Sábado"] = "Saturday"
        ,["Selecciona al menos un día para el horario."] = "Select at least one day for the schedule."
        ,["Indica un número entero de minutos para el periodo de confianza (entre 1 y 10.080)."] = "Enter a whole number of minutes for the trusted period (between 1 and 10,080)."
        ,["Indica un número entero de minutos para el cierre automático (entre 1 y 10.080)."] = "Enter a whole number of minutes for automatic closing (between 1 and 10,080)."
        ,["El periodo de confianza no puede superar el tiempo de cierre automático."] = "The trusted period cannot exceed the automatic closing time."
        ,["contraseña"] = "password"
        ,["sincronización de política pendiente"] = "policy synchronization pending"
        ,["manipulación"] = "tampering"
        ,["ProtectedApp no modificó tu configuración porque Windows no permitió leerla. Cierra cualquier programa que pueda estar usando los archivos de ProtectedApp y reinicia la aplicación."] = "ProtectedApp did not modify your configuration because Windows did not allow it to be read. Close any program that may be using ProtectedApp files and restart the app."
        ,["Se produjo un error inesperado al iniciar. No se ha modificado tu configuración. Consulta root-loaded-error.log en la carpeta local de ProtectedApp para ver el detalle."] = "An unexpected startup error occurred. Your configuration has not been modified. See root-loaded-error.log in ProtectedApp's local folder for details."
        ,["Protección eliminada y permisos originales restaurados"] = "Protection removed and original permissions restored"
        ,["Las carpetas ya no se desbloquean mediante permisos NTFS. Usa «Cifrar carpeta como bóveda» para convertirla en una bóveda cifrada."] = "Folders are no longer unlocked through NTFS permissions. Use “Encrypt folder as vault” to convert it into an encrypted vault."
        ,["El contenedor actual también es válido. Restaurar volverá a la versión anterior y puede descartar cambios posteriores. El actual se conservará con la marca 'replaced'."] = "The current container is also valid. Restoring will return to the previous version and may discard later changes. The current one will be kept with the 'replaced' suffix."
        ,["La copia anterior es válida. Sustituirá al contenedor ausente o dañado; el archivo dañado se conservará con la marca 'corrupt'."] = "The previous backup is valid. It will replace the missing or damaged container; the damaged file will be kept with the 'corrupt' suffix."
        ,["La versión seleccionada y el contenedor actual son válidos. No se ha modificado ningún archivo. Si restauras, volverás a esa fecha y el contenedor actual se conservará con la marca 'replaced'."] = "The selected version and current container are valid. No file has been modified. If you restore, you will return to that date and the current container will be kept with the 'replaced' suffix."
        ,["La versión seleccionada es válida y el contenedor actual no pudo validarse. No se ha modificado ningún archivo. Si restauras, el archivo actual se conservará con la marca 'corrupt'."] = "The selected version is valid and the current container could not be validated. No file has been modified. If you restore, the current file will be kept with the 'corrupt' suffix."
        ,["Revísalas para guardar sus cambios o descartarlas de forma segura."] = "Review them to save their changes or discard them safely."
        ,["ProtectedApp conservó sus carpetas de trabajo para no perder cambios."] = "ProtectedApp preserved their working folders to avoid losing changes."
        ,["Para reparar el servicio se desactivará su protección y se restaurarán sus permisos originales.\n\n"] = "To repair the service, its protection will be disabled and its original permissions restored.\n\n"
        ,["No se pudo ampliar el cierre automático"] = "Could not extend automatic closing"
        ,["Ejecución denegada"] = "Launch denied"
        ,["Manipulación detectada:"] = "Tampering detected:"
        ,["Actualización manual autorizada"] = "Manual update authorized"
        ,["Retiradas"] = "Removed"
        ,["La contraseña de la copia debe tener al menos 8 caracteres."] = "The backup password must be at least 8 characters."
        ,["La contraseña de la copia debe tener al menos 12 caracteres."] = "The backup password must be at least 12 characters."
        ,["La configuración es demasiado grande para crear una copia."] = "The configuration is too large to create a backup."
        ,["El archivo no es una copia válida de ProtectedApp."] = "The file is not a valid ProtectedApp backup."
        ,["El archivo no contiene una copia válida."] = "The file contains no valid backup."
        ,["La versión o el formato de la copia no es compatible."] = "The backup version or format is not supported."
        ,["El contenido de la copia no es válido."] = "Backup content is invalid."
        ,["La contraseña es incorrecta o el archivo ha sido modificado."] = "The password is incorrect or the file has been modified."
        ,["La versión interna de la copia no es compatible."] = "The backup internal version is not supported."
        ,["La copia contiene demasiadas reglas."] = "The backup contains too many rules."
        ,["La copia contiene demasiadas carpetas."] = "The backup contains too many folders."
        ,["La copia contiene demasiadas bóvedas."] = "The backup contains too many vaults."
        ,["La copia contiene una regla vacía."] = "The backup contains an empty rule."
        ,["La copia contiene una regla no válida."] = "The backup contains an invalid rule."
        ,["La copia contiene un horario sin días seleccionados."] = "The backup contains a schedule with no selected days."
        ,["La copia contiene una carpeta no válida."] = "The backup contains an invalid folder."
        ,["La copia contiene una referencia de bóveda no válida."] = "The backup contains an invalid vault reference."
        ,["La copia contiene una credencial de aplicación no válida."] = "The backup contains an invalid application credential."
        ,["La cabecera criptográfica de la copia no es válida."] = "The backup cryptographic header is invalid."
        ,["fecha desconocida"] = "unknown date"
        ,["modificado"] = "modified"
        ,["Guardian no devolvió una respuesta válida."] = "Guardian did not return a valid response."
        ,["Respuesta de Guardian no válida."] = "Invalid response from Guardian."
        ,["Guardian no está disponible."] = "Guardian is unavailable."
        ,["La aplicación ya no pertenece a la política."] = "The application no longer belongs to the policy."
        ,["Solicitud no válida."] = "Invalid request."
        ,["Solicitud vacía o demasiado grande."] = "The request is empty or too large."
        ,["Servidor ocupado; inténtalo de nuevo."] = "Server busy; try again."
        ,["La identidad del proceso cliente no coincide."] = "The client process identity does not match."
        ,["La sesión del proceso cliente no coincide."] = "The client process session does not match."
        ,["El origen de la solicitud no es válido."] = "The request source is not valid."
        ,["El diagnóstico requiere una sesión autenticada de ProtectedApp."] = "Diagnostics require an authenticated ProtectedApp session."
        ,["El diagnóstico se ejecutó hace unos segundos; espera antes de repetirlo."] = "Diagnostics ran a few seconds ago; wait before trying again."
        ,["Guardar"] = "Save"
        ,["Continuar"] = "Continue"
        ,["Eliminar"] = "Delete"
        ,["Descripción"] = "Description"
        ,["Opcional"] = "Optional"
        ,["Confirmar contraseña"] = "Confirm password"
        ,["Nueva contraseña propia"] = "New custom password"
        ,["Contraseña propia (opcional)"] = "Custom password (optional)"
        ,["Mínimo 8 caracteres"] = "At least 8 characters"
        ,["Mínimo 12 caracteres"] = "At least 12 characters"
        ,["Vacío = usar contraseña maestra"] = "Empty = use master password"
        ,["Mantener configuración actual"] = "Keep current settings"
        ,["Usar contraseña maestra"] = "Use master password"
        ,["Definir contraseña propia"] = "Set custom password"
        ,["Editar protección"] = "Edit protection"
        ,["Añadir aplicación"] = "Add application"
        ,["Proteger seleccionada"] = "Protect selected"
        ,["Elegir archivo…"] = "Choose file…"
        ,["Nueva bóveda cifrada"] = "New encrypted vault"
        ,["Convertir carpeta en bóveda"] = "Convert folder to vault"
        ,["Importar bóveda"] = "Import vault"
        ,["Copias programadas de bóvedas"] = "Scheduled vault backups"
        ,["Alerta remota de manipulación"] = "Remote tamper alert"
        ,["Contraseña actualizada"] = "Password updated"
        ,["Las contraseñas no coinciden."] = "Passwords do not match."
        ,["La contraseña debe tener al menos 8 caracteres."] = "The password must be at least 8 characters."
        ,["La contraseña debe tener al menos 12 caracteres."] = "The password must be at least 12 characters."
        ,["La contraseña propia debe tener al menos 6 caracteres."] = "The custom password must be at least 6 characters."
        ,["La contraseña propia debe tener al menos 12 caracteres."] = "The custom password must be at least 12 characters."
        ,["La nueva contraseña debe coincidir y tener al menos 6 caracteres."] = "The new password must match and be at least 6 characters."
        ,["La nueva contraseña debe coincidir y tener al menos 12 caracteres."] = "The new password must match and be at least 12 characters."
        ,["Contraseña incorrecta."] = "Incorrect password."
        ,["Contraseña incorrecta o contenedor no válido."] = "Incorrect password or invalid container."
        ,["No se puede recuperar todavía"] = "Cannot recover yet"
        ,["La recuperación no se completó"] = "Recovery did not complete"
        ,["No se pudo desmontar la bóveda"] = "Could not unmount vault"
        ,["Copias creadas"] = "Backups created"
        ,["Sin copias pendientes"] = "No pending backups"
        ,["No hay copias que limpiar"] = "No backups to clean up"
        ,["Protección administrada"] = "Managed protection"
        ,["Crea tu contraseña maestra"] = "Create your master password"
        ,["Nueva contraseña maestra"] = "New master password"
        ,["Confirmar contraseña actual"] = "Confirm current password"
        ,["No hay copia anterior"] = "There is no previous backup"
        ,["La copia anterior está dañada o incompleta"] = "The previous backup is damaged or incomplete"
        ,["Falta el contenedor principal; la copia puede recuperarse"] = "The primary container is missing; the backup can be restored"
        ,["El contenedor principal está dañado; la copia puede recuperarse"] = "The primary container is damaged; the backup can be restored"
        ,["Copia cifrada anterior disponible"] = "Previous encrypted backup available"
        ,["Contraseña maestra"] = "Master password"
        ,["Contraseña propia"] = "Custom password"
        ,["Sin protección"] = "Unprotected"
        ,["Bloqueada"] = "Locked"
        ,["Guardian rechazó la autenticación."] = "Guardian rejected the authentication."
        ,["Carpeta no disponible"] = "Folder unavailable"
        ,["La carpeta seleccionada ya no existe."] = "The selected folder no longer exists."
        ,["Cambiar ubicación…"] = "Change location…"
        ,["Opcional"] = "Optional"
        ,["Contraseña de la bóveda"] = "Vault password"
        ,["Bloquear automáticamente después de (minutos)"] = "Automatically lock after (minutes)"
        ,["Se creará y verificará una bóveda .pavault cifrada. La carpeta original solo se eliminará si lo confirmas después de verificar la bóveda."] = "An encrypted .pavault vault will be created and verified. The original folder will only be removed if you confirm it after verifying the vault."
        ,["Guardar"] = "Save"
        ,["La contraseña de la bóveda debe tener al menos 8 caracteres."] = "The vault password must be at least 8 characters."
        ,["La contraseña de la bóveda debe tener al menos 12 caracteres."] = "The vault password must be at least 12 characters."
        ,["Guarda la bóveda fuera de la carpeta que se va a cifrar."] = "Save the vault outside the folder being encrypted."
        ,["Ya existe una bóveda registrada con esa ubicación. Elige otra ubicación o retira primero la referencia anterior."] = "A vault is already registered at that location. Choose another location or remove the previous reference first."
        ,["Ya existe un archivo con ese nombre. Cambia el nombre o la ubicación."] = "A file with that name already exists. Change the name or location."
        ,["No se pudo convertir"] = "Could not convert"
        ,["No se pudo crear y verificar la bóveda cifrada."] = "Could not create and verify the encrypted vault."
        ,["Bóveda cifrada creada desde"] = "Encrypted vault created from"
        ,["Bóveda verificada"] = "Vault verified"
        ,["La bóveda se ha creado y verificado correctamente. Para completar la conversión y evitar conservar una copia sin cifrar, elimina ahora la carpeta original. Esta acción no se puede deshacer."] = "The vault was created and verified successfully. To complete the conversion and avoid retaining an unencrypted copy, remove the original folder now. This action cannot be undone."
        ,["Conversión pendiente"] = "Conversion pending"
        ,["La bóveda ya está protegida, pero la carpeta original sigue existiendo sin cifrar. Elimínala manualmente cuando hayas comprobado el contenido."] = "The vault is already protected, but the original folder still exists unencrypted. Remove it manually after checking the contents."
        ,["Carpeta original eliminada tras verificar la conversión"] = "Original folder removed after verifying the conversion"
        ,["No se pudo eliminar la carpeta original. Revísala y elimínala manualmente cuando proceda."] = "Could not remove the original folder. Review it and remove it manually when appropriate."
        ,["Editar carpeta protegida"] = "Edit protected folder"
        ,["No se pudieron guardar los cambios"] = "Could not save changes"
        ,["Guardian rechazó la configuración."] = "Guardian rejected the configuration."
        ,["Protección de carpeta actualizada"] = "Folder protection updated"
        ,["Autorizar conversión de carpeta"] = "Authorize folder conversion"
        ,["Introduce la contraseña para desbloquear temporalmente la carpeta"] = "Enter the password to temporarily unlock the folder"
        ,["Contraseña incorrecta al intentar desbloquear la carpeta"] = "Incorrect password while trying to unlock the folder"
        ,["No se pudo bloquear la carpeta"] = "Could not lock folder"
        ,["Guardian no confirmó el bloqueo."] = "Guardian did not confirm the lock."
        ,["Eliminar protección de carpeta"] = "Remove folder protection"
        ,["No se pudo restaurar la carpeta"] = "Could not restore folder"
        ,["Guardian no pudo restaurar sus permisos originales."] = "Guardian could not restore its original permissions."
        ,["Guardian rechazó el cambio."] = "Guardian rejected the change."
        ,["Protección de carpeta activada"] = "Folder protection enabled"
        ,["Protección desactivada y permisos restaurados"] = "Protection disabled and permissions restored"
        ,["Guardian es necesario"] = "Guardian is required"
        ,["La protección de carpetas requiere que el servicio Guardian esté instalado, actualizado y en ejecución."] = "Folder protection requires the Guardian service to be installed, up to date, and running."
        ,["Combinación de teclas"] = "Key combination"
        ,["Pulsa la combinación"] = "Press the key combination"
        ,["El atajo funciona aunque la ventana esté oculta. Ejecuta el bloqueo inmediato: cierra aplicaciones protegidas, desmonta bóvedas y bloquea Windows."] = "The shortcut works even when the window is hidden. It performs the immediate lock: closes protected apps, unmounts vaults, and locks Windows."
        ,["El atajo funciona aunque la ventana esté oculta. Solicita primero el cierre normal de las aplicaciones protegidas; tras 5 segundos, fuerza el cierre de las que sigan abiertas. Después desmonta bóvedas y bloquea Windows."] = "The shortcut works even when the window is hidden. It first requests normal closing for protected applications; after 5 seconds, it forces any that remain open to close. It then unmounts vaults and locks Windows."
        ,["Solicita el cierre normal y, tras 5 segundos, fuerza las aplicaciones que sigan abiertas."] = "Requests normal closing and, after 5 seconds, forces any applications that remain open to close."
        ,["Haz clic en el campo y pulsa la combinación. Debe incluir Ctrl, Alt o Mayús y una letra, número o F1 a F12."] = "Click the field and press the combination. It must include Ctrl, Alt, or Shift and a letter, number, or F1 through F12."
        ,["Atajo de bloqueo inmediato"] = "Immediate lock shortcut"
        ,["No se pudo configurar el atajo"] = "Could not configure shortcut"
        ,["Atajo no disponible"] = "Shortcut unavailable"
        ,["Windows no pudo registrar esta combinación; probablemente ya la usa otra aplicación."] = "Windows could not register this combination; another app is probably already using it."
        ,["Usa al menos Ctrl, Alt o Mayús y una tecla, por ejemplo Ctrl+Alt+L."] = "Use at least Ctrl, Alt, or Shift and a key, for example Ctrl+Alt+L."
        ,["La tecla debe ser una letra, un número o F1 a F12."] = "The key must be a letter, number, or F1 through F12."
        ,["Guardian no disponible"] = "Guardian unavailable"
        ,["No se puede aplicar el bloqueo inmediato mientras el servicio Guardian no esté activo. Repara el servicio desde Configuración."] = "Immediate lock cannot be applied while the Guardian service is inactive. Repair the service from Settings."
        ,["No se pudo bloquear"] = "Could not lock"
        ,["Guardian no confirmó la revocación de accesos."] = "Guardian did not confirm access revocation."
        ,["El bloqueo inmediato requiere que Guardian esté activo. Repara el servicio desde Configuración."] = "Immediate lock requires Guardian to be active. Repair the service from Settings."
        ,["No se pudo aplicar el bloqueo inmediato"] = "Could not apply immediate lock"
        ,["Guardian no confirmó el bloqueo de accesos."] = "Guardian did not confirm access locking."
        ,["Copia creada"] = "Backup created"
        ,["La configuración se ha exportado correctamente. Guarda también la contraseña de la copia: no puede recuperarse si se pierde."] = "The configuration was exported successfully. Keep the backup password too: it cannot be recovered if lost."
        ,["No se pudo crear la copia"] = "Could not create backup"
        ,["Hay bóvedas abiertas"] = "There are open vaults"
        ,["Guarda y bloquea todas las bóvedas antes de reemplazar la configuración."] = "Save and lock all vaults before replacing the configuration."
        ,["No se pudo abrir la copia"] = "Could not open backup"
        ,["Restaurar copia"] = "Restore backup"
        ,["Reemplazar configuración"] = "Replace configuration"
        ,["Copia restaurada"] = "Backup restored"
        ,["La configuración se restauró localmente. Guardian la sincronizará después de volver a validar la contraseña maestra."] = "The configuration was restored locally. Guardian will synchronize it after the master password is validated again."
        ,["No se pudo restaurar la copia"] = "Could not restore backup"
        ,["Proteger copia"] = "Protect backup"
        ,["Abrir copia cifrada"] = "Open encrypted backup"
        ,["Contraseña de la copia"] = "Backup password"
        ,["Repite la contraseña"] = "Repeat password"
        ,["Esta contraseña cifra el archivo y puede ser distinta de la contraseña maestra. Necesitarás conservarla para restaurar la copia."] = "This password encrypts the file and can differ from the master password. You will need to keep it to restore the backup."
        ,["Introduce la contraseña que se utilizó al crear esta copia."] = "Enter the password used to create this backup."
        ,["No se pudo iniciar la desinstalación"] = "Could not start uninstallation"
        ,["Esta copia se ejecuta desde una carpeta publicada y no fue instalada con Setup.exe. Instálala primero o retira manualmente el servicio desde Configuración."] = "This copy runs from a published folder and was not installed with Setup.exe. Install it first or remove the service manually from Settings."
        ,["No se pudo seleccionar el instalador"] = "Could not select installer"
        ,["Verificando firma, identidad y versión…"] = "Verifying signature, identity, and version…"
        ,["Actualización rechazada"] = "Update rejected"
        ,["El instalador no ha superado la validación de seguridad."] = "The installer did not pass security validation."
        ,["Las bóvedas abiertas se guardarán y cerrarán antes de iniciar Setup. Windows solicitará permiso de administrador."] = "Open vaults will be saved and closed before starting Setup. Windows will request administrator permission."
        ,["Actualización verificada"] = "Verified update"
        ,["Instalar actualización"] = "Install update"
        ,["Autorizar actualización de ProtectedApp"] = "Authorize ProtectedApp update"
        ,["Iniciando actualización"] = "Starting update"
        ,["No se pudo actualizar"] = "Could not update"
        ,["Windows no pudo iniciar el instalador seleccionado."] = "Windows could not start the selected installer."
        ,["No se pudo iniciar la actualización"] = "Could not start update"
        ,["Comprobando la protección"] = "Checking protection"
        ,["No se pudo completar el diagnóstico"] = "Could not complete diagnostics"
        ,["Autorizar reparación de ProtectedApp"] = "Authorize ProtectedApp repair"
        ,["Reparando componentes de protección"] = "Repairing protection components"
        ,["Windows solicitará permiso de administrador para reinstalar las capas protegidas."] = "Windows will request administrator permission to reinstall protected layers."
        ,["Reparación completada"] = "Repair completed"
        ,["Reparación incompleta"] = "Repair incomplete"
        ,["Guardian, Gate, la política y la tarea SYSTEM se han comprobado correctamente."] = "Guardian, Gate, the policy, and the SYSTEM task were checked successfully."
        ,["Guardian no disponible"] = "Guardian unavailable"
        ,["La autenticación se ha cerrado de forma segura porque el motor protegido no respondió."] = "Authentication was safely closed because the protected engine did not respond."
        ,["Introduce tu contraseña maestra para continuar"] = "Enter your master password to continue"
        ,["Contraseña maestra incorrecta"] = "Incorrect master password"
        ,["No se pudo iniciar"] = "Could not start"
        ,["Guardian rechazó la autorización."] = "Guardian rejected authorization."
        ,["No se pudo reparar el servicio"] = "Could not repair service"
        ,["Servicio no disponible"] = "Service unavailable"
        ,["Los archivos del servicio no están junto a esta publicación. Vuelve a publicar ProtectedApp y copia toda la carpeta."] = "The service files are not next to this publication. Publish ProtectedApp again and copy the entire folder."
        ,["Solicitando instalación…"] = "Requesting installation…"
        ,["Solicitando desinstalación…"] = "Requesting uninstallation…"
        ,["No se pudo cambiar el servicio"] = "Could not change service"
        ,["No se pudo recuperar"] = "Could not recover"
        ,["Guardian no devolvió la política protegida."] = "Guardian did not return the protected policy."
        ,["Recuperar configuración protegida"] = "Recover protected configuration"
        ,["Introduce la contraseña maestra para restaurar las reglas desde Guardian"] = "Enter the master password to restore rules from Guardian"
        ,["Contraseña maestra incorrecta al recuperar la configuración"] = "Incorrect master password while recovering configuration"
        ,["No hay eventos"] = "No events"
        ,["Actividad exportada"] = "Activity exported"
        ,["No se pudo exportar"] = "Could not export"
        ,["Protegiendo…"] = "Protecting…"
        ,["Consulta segura"] = "Safe viewing"
        ,["Cambios pendientes"] = "Changes pending"
        ,["Cambios recuperables"] = "Recoverable changes"
        ,["Cerrada"] = "Closed"
        ,["Copias programadas sin configurar"] = "Scheduled backups not configured"
        ,["⚠ Copia programada pendiente"] = "⚠ Scheduled backup pending"
        ,["⚠ Copia programada vencida"] = "⚠ Scheduled backup overdue"
        ,["⚠ Error al crear la copia programada"] = "⚠ Error creating scheduled backup"
        ,["fecha desconocida"] = "unknown date"
        ,["modificado"] = "modified"
        ,["Apertura incompleta"] = "Incomplete opening"
        ,["No asociada"] = "Unassociated"
        ,["Falta el contenedor"] = "Container missing"
        ,["Lista para recuperar"] = "Ready to recover"
        ,["Indica un nombre."] = "Enter a name."
        ,["Introduce un nombre."] = "Enter a name."
        ,["El tiempo debe estar entre 1 y 10.080 minutos."] = "The time must be between 1 and 10,080 minutes."
        ,["La inactividad debe estar entre 0 y 10.080 minutos."] = "Inactivity must be between 0 and 10,080 minutes."
        ,["La nueva contraseña debe tener al menos 8 caracteres."] = "The new password must be at least 8 characters."
        ,["Las nuevas contraseñas no coinciden."] = "The new passwords do not match."
        ,["Bloquear grupo seleccionado"] = "Lock selected group"
        ,["Sin atajo configurado."] = "No shortcut configured."
        ,["Comprobando estado…"] = "Checking status…"
        ,["Guardian y seguridad"] = "Guardian and security"
        ,["Escribe DESCARTAR para confirmar"] = "Type DISCARD to confirm"
        ,["DESCARTAR"] = "DISCARD"
        ,["Bóveda creada; carpeta original conservada"] = "Vault created; original folder retained"
        ,["Bóveda creada; limpieza pendiente"] = "Vault created; cleanup pending"
        ,["No se pudo preparar la carpeta original para eliminarla. No se eliminó ningún archivo. Cierra las aplicaciones que la usen y revisa sus permisos:"] = "Could not prepare the original folder for deletion. No files were deleted. Close applications using it and check its permissions:"
        ,["La carpeta original se movió a una ubicación de limpieza antes de borrar su contenido, pero Windows no terminó la operación. La bóveda cifrada ya fue verificada. Revisa y elimina manualmente la carpeta restante:"] = "The original folder was moved to a cleanup location before its contents were deleted, but Windows did not complete the operation. The encrypted vault has already been verified. Review and remove the remaining folder manually:"
        ,["Windows denegó el acceso al recurso."] = "Windows denied access to the resource."
        ,["Windows no pudo completar la operación porque el archivo o recurso está en uso."] = "Windows could not complete the operation because the file or resource is in use."
        ,["No se pudo completar la operación."] = "Could not complete the operation."
        ,["Comprobación"] = "Check"
        ,["Introduce tu contraseña maestra para autorizar la desinstalación"] = "Enter your master password to authorize uninstallation"
        ,["Desinstalar ProtectedApp"] = "Uninstall ProtectedApp"
        ,["Recursos de interfaz cargados correctamente."] = "User-interface resources loaded successfully."
        ,["No se pudo exportar el registro de actividad."] = "Could not export the activity log."
        ,["Guardian rechazó el inicio."] = "Guardian rejected the launch."
        ,["No se pudo iniciar la aplicación."] = "Could not start the application."
        ,["No se pudo exportar la copia de seguridad."] = "Could not export the backup."
        ,["No se pudo revisar la recuperación de bóvedas."] = "Could not check vault recovery."
        ,["ProtectedApp encontró un error al proteger las bóvedas tras un evento de Windows. Sus carpetas de trabajo se conservaron para no perder cambios."] = "ProtectedApp encountered an error while protecting vaults after a Windows event. Their working folders were retained to prevent data loss."
        ,["Error al proteger las bóvedas ante un evento de Windows."] = "Error protecting vaults after a Windows event."
    };

    // A reverse lookup is only safe for one-to-one translations.  English
    // labels such as "OK" can originate from several Spanish labels; choosing
    // an arbitrary first value corrupts the source text on a language switch.
    private static readonly IReadOnlyDictionary<string, string[]> SpanishCandidates = English
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray(), StringComparer.Ordinal);

    private sealed record RenderedText(string Source, string Rendered);
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, RenderedText>> ElementSources = new();
    private static readonly string SystemLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public static string CurrentLanguage { get; private set; } = "es";
    public static bool IsEnglish => CurrentLanguage == "en";
    public static event Action? LanguageChanged;

    public static string NormalizeLanguage(string? language)
    {
        if (language?.Equals("en", StringComparison.OrdinalIgnoreCase) == true) return "en";
        if (language?.Equals("es", StringComparison.OrdinalIgnoreCase) == true) return "es";
        return "system";
    }

    public static void SetLanguage(string? preference)
    {
        CurrentLanguage = NormalizeLanguage(preference) switch
        {
            "en" => "en",
            "es" => "es",
            _ => SystemLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? "en" : "es"
        };
        var culture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "es-ES");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        LanguageChanged?.Invoke();
    }

    public static string T(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (IsEnglish)
            return English.TryGetValue(value, out var translated) ? translated : TranslateDynamicSpanish(value);
        return SpanishCandidates.TryGetValue(value, out var candidates) && candidates.Length == 1
            ? candidates[0]
            : TranslateDynamicEnglish(value);
    }

    // Old logs may contain only a rendered message. Recover only known,
    // unambiguous sources; preserve unknown text rather than inventing it.
    public static string RecoverSource(string value)
    {
        if (English.ContainsKey(value)) return value;
        return SpanishCandidates.TryGetValue(value, out var candidates) && candidates.Length == 1
            ? candidates[0] : TranslateDynamicEnglish(value);
    }

    public static string T(DependencyObject owner, string property, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var sources = ElementSources.GetOrCreateValue(owner);
        var source = sources.TryGetValue(property, out var previous) && value == previous.Rendered
            ? previous.Source : value;
        var rendered = T(source);
        sources[property] = new(source, rendered);
        return rendered;
    }

    /// <summary>
    /// Keeps operating-system exception text out of the localized UI. Windows
    /// returns this text in its own display language, which need not match the
    /// language selected in ProtectedApp. Callers should still log the original
    /// exception for diagnostics.
    /// </summary>
    public static string UserFacingError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => T("Windows denegó el acceso al recurso."),
        IOException => T("Windows no pudo completar la operación porque el archivo o recurso está en uso."),
        _ => T("No se pudo completar la operación.")
    };

    /// <summary>
    /// Localizes a known service message when it belongs to the catalogue and
    /// otherwise presents a localized fallback instead of leaking a Windows or
    /// service-host language into the UI.
    /// </summary>
    public static string UserFacingMessage(string? message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message)) return T(fallback);
        var translated = T(message);
        return English.ContainsKey(message)
               || (SpanishCandidates.TryGetValue(message, out var candidates) && candidates.Length == 1)
               || !StringComparer.Ordinal.Equals(translated, message)
            ? translated
            : T(fallback);
    }

    // Activity, backup, and status records include runtime values.  Keeping
    // their templates here prevents a Spanish prefix leaking into the English
    // UI simply because a count, time, name, or path was interpolated.
    private static string TranslateDynamicSpanish(string value)
    {
        var match = Regex.Match(value, @"^Atajo global: (.+)$");
        if (match.Success) return $"Global shortcut: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo preparar la carpeta original para eliminarla\. No se eliminó ningún archivo\. Cierra las aplicaciones que la usen y revisa sus permisos:\r?\n(.+)$");
        if (match.Success) return $"Could not prepare the original folder for deletion. No files were deleted. Close applications using it and check its permissions:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^La carpeta original se movió a una ubicación de limpieza antes de borrar su contenido, pero Windows no terminó la operación\. La bóveda cifrada ya fue verificada\. Revisa y elimina manualmente la carpeta restante:\r?\n(.+)$");
        if (match.Success) return $"The original folder was moved to a cleanup location before its contents were deleted, but Windows did not complete the operation. The encrypted vault has already been verified. Review and remove the remaining folder manually:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo eliminar la carpeta original porque Windows denegó el acceso\. Cierra las aplicaciones que la usen y revisa sus permisos antes de eliminarla manualmente:\r?\n(.+)$");
        if (match.Success) return $"Could not remove the original folder because Windows denied access. Close applications using it and check its permissions before removing it manually:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^(\d+) carpetas protegidas disponibles\.$");
        if (match.Success) return $"{match.Groups[1].Value} protected folder(s) available.";
        match = Regex.Match(value, @"^(\d+) carpetas protegidas ya no existen o no están disponibles\.$");
        if (match.Success) return $"{match.Groups[1].Value} protected folder(s) no longer exist or are unavailable.";
        match = Regex.Match(value, @"^(\d+) archivos \.pavault no están disponibles\.$");
        if (match.Success) return $"{match.Groups[1].Value} .pavault file(s) are unavailable.";
        match = Regex.Match(value, @"^(\d+) bóvedas tienen cambios pendientes de comprobar antes de abrirse\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault(s) have pending changes to check before opening.";
        match = Regex.Match(value, @"^(\d+) bóvedas disponibles\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault(s) available.";
        match = Regex.Match(value, @"^(\d+) bóveda\(s\) figuran como abiertas, pero su unidad no responde: (.+)\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault(s) are marked as open, but their drive does not respond: {match.Groups[2].Value}.";
        match = Regex.Match(value, @"^(\d+) unidad\(es\) no exponen el sistema de archivos PAVLT003: (.+)\.$");
        if (match.Success) return $"{match.Groups[1].Value} drive(s) do not expose the PAVLT003 file system: {match.Groups[2].Value}.";
        match = Regex.Match(value, @"^(\d+) bóveda\(s\) abierta\(s\) y accesible\(s\) mediante PAVLT003\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault(s) open and accessible through PAVLT003.";
        match = Regex.Match(value, @"^(\d+) bóveda\(s\) conservan un diario de una sesión anterior\. Ábrelas y vuelve a bloquearlas para consolidar o revisar los cambios\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault(s) retain a journal from a previous session. Open and lock them again to consolidate or review the changes.";
        match = Regex.Match(value, @"^No hay recuperación pendiente\. (\d+) bóveda\(s\) tienen una copia cifrada anterior disponible\.$");
        if (match.Success) return $"No recovery is pending. {match.Groups[1].Value} vault(s) have a previous encrypted backup available.";
        match = Regex.Match(value, @"^Bóveda cifrada creada desde (.+)$");
        if (match.Success) return $"Encrypted vault created from {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Carpeta desbloqueada durante (\d+) min$");
        if (match.Success) return $"Folder unlocked for {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^Bloqueo automático del panel configurado en (\d+) min$");
        if (match.Success) return $"Automatic panel lock set to {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^Panel bloqueado automáticamente tras (\d+) min sin actividad$");
        if (match.Success) return $"Panel locked automatically after {match.Groups[1].Value} min of inactivity";
        match = Regex.Match(value, @"^Bóveda restaurada desde la copia programada del (.+)$");
        if (match.Success) return $"Vault restored from the scheduled backup of {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Versión programada comprobada correctamente \((.+)\)$");
        if (match.Success) return $"Scheduled version checked successfully ({match.Groups[1].Value})";
        match = Regex.Match(value, @"^Detectados (\d+) trabajo\(s\) de bóveda pendientes de recuperación$");
        if (match.Success) return $"{match.Groups[1].Value} pending vault recovery task(s) detected";
        match = Regex.Match(value, @"^Eliminadas (\d+) referencia\(s\) de bóvedas sin contenedor disponible$");
        if (match.Success) return $"Removed {match.Groups[1].Value} vault reference(s) without an available container";
        match = Regex.Match(value, @"^Registro de actividad exportado con (\d+) eventos$");
        if (match.Success) return $"Activity log exported with {match.Groups[1].Value} event(s)";
        match = Regex.Match(value, @"^Copia de seguridad restaurada con (\d+) aplicaciones, (\d+) carpetas y (\d+) bóvedas$");
        if (match.Success) return $"Backup restored with {match.Groups[1].Value} applications, {match.Groups[2].Value} folders, and {match.Groups[3].Value} vaults";
        match = Regex.Match(value, @"^Copia de seguridad cifrada exportada con (\d+) aplicaciones y (\d+) carpetas$");
        if (match.Success) return $"Encrypted backup exported with {match.Groups[1].Value} applications and {match.Groups[2].Value} folders";
        match = Regex.Match(value, @"^Cierre automático ampliado (\d+) min$");
        if (match.Success) return $"Automatic closing extended by {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^Quedan (\d+) componentes que requieren revisión\.$");
        if (match.Success) return $"{match.Groups[1].Value} component(s) still require review.";
        match = Regex.Match(value, @"^Se han exportado (\d+) eventos visibles\. La búsqueda y el filtro actuales se han respetado\.$");
        if (match.Success) return $"{match.Groups[1].Value} visible event(s) were exported. The current search and filter were retained.";
        match = Regex.Match(value, @"^Se eliminarán permanentemente los (\d+) eventos guardados en este equipo\.$");
        if (match.Success) return $"The {match.Groups[1].Value} events stored on this computer will be permanently deleted.";
        match = Regex.Match(value, @"^(.+) podrá volver a abrirse sin contraseña\.$");
        if (match.Success) return $"{match.Groups[1].Value} can be opened again without a password.";
        match = Regex.Match(value, @"^Se revocará únicamente el acceso de (.+) y se cerrarán sus procesos abiertos\. El trabajo no guardado podría perderse\.$");
        if (match.Success) return $"Only access to {match.Groups[1].Value} will be revoked and its open processes will be closed. Unsaved work may be lost.";
        match = Regex.Match(value, @"^Protección activada en lote para '(.+)' \((\d+) aplicaciones\)$");
        if (match.Success) return $"Batch protection enabled for '{match.Groups[1].Value}' ({match.Groups[2].Value} applications)";
        match = Regex.Match(value, @"^Protección desactivada en lote para '(.+)' \((\d+) aplicaciones\)$");
        if (match.Success) return $"Batch protection disabled for '{match.Groups[1].Value}' ({match.Groups[2].Value} applications)";
        match = Regex.Match(value, @"^Bloqueo inmediato aplicado; (\d+) procesos protegidos finalizados, (\d+) carpetas y (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Immediate lock applied; {match.Groups[1].Value} protected process(es) ended, {match.Groups[2].Value} folder(s), and {match.Groups[3].Value} vault(s) locked";
        match = Regex.Match(value, @"^Aplicado; (\d+) procesos protegidos finalizados, (\d+) carpetas y (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Applied; {match.Groups[1].Value} protected process(es) ended, {match.Groups[2].Value} folder(s), and {match.Groups[3].Value} vault(s) locked";
        match = Regex.Match(value, @"^Atajo aplicado; (\d+) procesos protegidos finalizados, (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Shortcut applied; {match.Groups[1].Value} protected process(es) ended, {match.Groups[2].Value} vault(s) locked";
        match = Regex.Match(value, @"^Atajo aplicado; (\d+) aplicaciones cerradas normalmente, (\d+) cierres forzados, (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Shortcut applied; {match.Groups[1].Value} application(s) closed normally, {match.Groups[2].Value} forcibly closed, {match.Groups[3].Value} vault(s) locked";
        match = Regex.Match(value, @"^Bloqueo inmediato aplicado; (\d+) aplicaciones cerradas normalmente, (\d+) cierres forzados, (\d+) carpetas y (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Immediate lock applied; {match.Groups[1].Value} application(s) closed normally, {match.Groups[2].Value} forcibly closed, {match.Groups[3].Value} folder(s) and {match.Groups[4].Value} vault(s) locked";
        match = Regex.Match(value, @"^Aplicado; (\d+) aplicaciones cerradas normalmente, (\d+) cierres forzados, (\d+) carpetas y (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Applied; {match.Groups[1].Value} application(s) closed normally, {match.Groups[2].Value} forcibly closed, {match.Groups[3].Value} folder(s) and {match.Groups[4].Value} vault(s) locked";
        match = Regex.Match(value, @"^Atajo aplicado; (\d+) solicitudes de cierre enviadas a aplicaciones protegidas, (\d+) bóvedas bloqueadas$");
        if (match.Success) return $"Shortcut applied; close requests sent to {match.Groups[1].Value} protected application(s), {match.Groups[2].Value} vault(s) locked";
        match = Regex.Match(value, @"^Letra preferida para bóvedas configurada en ([A-Z]):$");
        if (match.Success) return $"Preferred vault drive letter set to {match.Groups[1].Value}:";
        match = Regex.Match(value, @"^Limpieza de copias de bóvedas: (\d+) eliminadas, (.+) liberados$");
        if (match.Success) return $"Vault backup cleanup: {match.Groups[1].Value} deleted, {match.Groups[2].Value} freed";
        match = Regex.Match(value, @"^Se crearon (\d+) copias de bóveda\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault backup(s) created.";
        match = Regex.Match(value, @"^Versión instalada (.+)$");
        if (match.Success) return $"Installed version {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo desmontar la unidad virtual: (.+)$");
        if (match.Success) return $"Could not unmount the virtual drive: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo restaurar automáticamente el archivo anterior: (.+)$");
        if (match.Success) return $"Could not automatically restore the previous file: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo iniciar: (.+)$");
        if (match.Success) return $"Could not start: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Guardian rechazó el inicio: (.+)$");
        if (match.Success) return $"Guardian rejected the launch: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo revisar la recuperación de bóvedas: (.+)$");
        if (match.Success) return $"Could not review vault recovery: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Recuperación detenida por la comprobación previa: (.+)$");
        if (match.Success) return $"Recovery stopped by the preliminary check: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo restaurar una versión programada: (.+)$");
        if (match.Success) return $"Could not restore scheduled version: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Falló la recuperación después de comprobar la integridad: (.+)$");
        if (match.Success) return $"Recovery failed after integrity checking: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Falló la recuperación automática de la copia anterior: (.+)$");
        if (match.Success) return $"Automatic recovery from the previous backup failed: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Bóveda restaurada e importada; el contenedor dañado se conservó en (.+)$");
        if (match.Success) return $"Vault restored and imported; the damaged container was preserved at {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Copia anterior restaurada al abrir; el contenedor sustituido se conservó en (.+)$");
        if (match.Success) return $"Previous backup restored while opening; the replaced container was preserved at {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Contenedor recuperado desde la copia anterior; el archivo sustituido se conservó en (.+)$");
        if (match.Success) return $"Container recovered from the previous backup; the replaced file was preserved at {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Bóveda eliminada permanentemente junto con (\d+) copia\(s\) cifrada\(s\)$");
        if (match.Success) return $"Vault permanently deleted along with {match.Groups[1].Value} encrypted backup(s)";
        match = Regex.Match(value, @"^Detectadas (\d+) copia\(s\) de bóveda que requieren recuperación$");
        if (match.Success) return $"{match.Groups[1].Value} vault backup(s) requiring recovery detected";
        match = Regex.Match(value, @"^Detectados (\d+) trabajo\(s\) de bóveda pendientes de recuperación$");
        if (match.Success) return $"{match.Groups[1].Value} pending vault recovery task(s) detected";
        match = Regex.Match(value, @"^Bóveda bloqueada automáticamente(.*)$");
        if (match.Success) return $"Vault locked automatically{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Consulta segura cerrada automáticamente(.*)$");
        if (match.Success) return $"Safe viewing closed automatically{match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo desmontar automáticamente la unidad virtual (.+)$");
        if (match.Success) return $"Could not automatically unmount the virtual drive {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo aplicar el bloqueo automático (.+); la carpeta de trabajo se conservó$");
        if (match.Success) return $"Could not apply automatic lock {match.Groups[1].Value}; the working folder was preserved";
        match = Regex.Match(value, @"^La bóveda está abierta en:\r?\n(.+)$");
        if (match.Success) return $"The vault is open at:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^(\d+) bóveda\(s\) guardadas y bloqueadas automáticamente por (.+)$");
        if (match.Success) return $"{match.Groups[1].Value} vault(s) automatically saved and locked because of {match.Groups[2].Value}";
        match = Regex.Match(value, @"^No se pudo guardar y bloquear la bóveda durante (.+); se conservó su carpeta de trabajo$");
        if (match.Success) return $"Could not save and lock the vault during {match.Groups[1].Value}; its working folder was preserved";
        match = Regex.Match(value, @"^Error al proteger las bóvedas ante un evento de Windows: (.+)$");
        if (match.Success) return $"Error protecting vaults after a Windows event: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^(.+) · (\d+) evento\(s\) más recientes$");
        if (match.Success) return $"{match.Groups[1].Value} · {match.Groups[2].Value} most recent event(s)";
        match = Regex.Match(value, @"^Hay (\d+) bóvedas cuyo contenedor principal está ausente o dañado\. ProtectedApp ha encontrado copias cifradas anteriores para recuperarlas\.$");
        if (match.Success) return $"There are {match.Groups[1].Value} vaults whose primary container is missing or damaged. ProtectedApp found previous encrypted backups to recover them.";
        match = Regex.Match(value, @"^Hay (\d+) bóveda\(s\) con el contenedor principal ausente o dañado; (\d+) tienen una copia con estructura recuperable y las demás requieren revisión manual\.$");
        if (match.Success) return $"There are {match.Groups[1].Value} vault(s) with a missing or damaged primary container; {match.Groups[2].Value} have a structurally recoverable backup and the rest require manual review.";
        match = Regex.Match(value, @"^(.+) contiene cambios que todavía no se han consolidado en el archivo cifrado\. Al desmontarla se guardarán de forma segura antes de cerrar la unidad\.$");
        if (match.Success) return $"{match.Groups[1].Value} has changes not yet consolidated into the encrypted file. Unmounting it will save them safely before closing the drive.";
        match = Regex.Match(value, @"^Se eliminará permanentemente el contenedor de (.+)\. Esta acción no se puede deshacer\.$");
        if (match.Success) return $"The container for {match.Groups[1].Value} will be permanently deleted. This action cannot be undone.";
        match = Regex.Match(value, @"^Autorizar eliminación permanente de (.+)$");
        if (match.Success) return $"Authorize permanent deletion of {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Se eliminará la copia del (.+)\. Esta acción no se puede deshacer\.$");
        if (match.Success) return $"The backup from {match.Groups[1].Value} will be permanently deleted. This action cannot be undone.";
        match = Regex.Match(value, @"^Versión programada comprobada correctamente \((.+)\)$");
        if (match.Success) return $"Scheduled version checked successfully ({match.Groups[1].Value})";
        match = Regex.Match(value, @"^Bóveda restaurada desde la copia programada del (.+)$");
        if (match.Success) return $"Vault restored from the scheduled backup of {match.Groups[1].Value}";
        match = Regex.Match(value, @"^(.+) para (.+)\. La copia puede restaurarse desde la sección de bóvedas\.$");
        if (match.Success) return $"{match.Groups[1].Value} for {match.Groups[2].Value}. The backup can be restored from the vaults section.";
        match = Regex.Match(value, @"^El contenedor principal de (.+) está ausente o dañado, pero existe una copia cifrada anterior que puede recuperarse\.$");
        if (match.Success) return $"The primary container for {match.Groups[1].Value} is missing or damaged, but a previous encrypted backup can be recovered.";
        match = Regex.Match(value, @"^Windows inició (.+), pero no se pudo guardar y bloquear al menos una bóveda\. (.+)$", RegexOptions.Singleline);
        if (match.Success) return $"Windows started {match.Groups[1].Value}, but at least one vault could not be saved and locked. {match.Groups[2].Value}";
        match = Regex.Match(value, @"^ProtectedApp encontró un error al proteger las bóvedas tras un evento de Windows: (.+)$");
        if (match.Success) return $"ProtectedApp encountered an error protecting vaults after a Windows event: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^(.+) se cerrará automáticamente en menos de un minuto\.$");
        if (match.Success) return $"{match.Groups[1].Value} will close automatically in less than one minute.";
        match = Regex.Match(value, @"^Cierre automático ampliado (\d+) min$");
        if (match.Success) return $"Automatic closing extended by {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^No se pudo ampliar el cierre automático: (.+)$");
        if (match.Success) return $"Could not extend automatic closing: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Ejecución denegada: (.+)$");
        if (match.Success) return $"Launch denied: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Manipulación detectada: (.+)$");
        if (match.Success) return $"Tampering detected: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^El instalador de Guardian terminó con el código (.+)\. Vuelve a intentarlo; el próximo intento mostrará el detalle si Windows rechaza algún paso\.$");
        if (match.Success) return $"Guardian installer finished with code {match.Groups[1].Value}. Try again; the next attempt will show details if Windows rejects a step.";
        match = Regex.Match(value, @"^Se restaurarán los permisos originales de (.+)\. Sus archivos no se eliminarán\.$");
        if (match.Success) return $"The original permissions for {match.Groups[1].Value} will be restored. Its files will not be deleted.";
        match = Regex.Match(value, @"^Autorizar eliminación de (.+)$");
        if (match.Success) return $"Authorize deletion of {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Autorizar protección de (.+)$");
        if (match.Success) return $"Authorize protection of {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Autorizar restauración de (.+)$");
        if (match.Success) return $"Authorize restoration of {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Activar protección de (.+)$");
        if (match.Success) return $"Enable protection for {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Desactivar protección de (.+)$");
        if (match.Success) return $"Disable protection for {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Se reemplazarán las (\d+) aplicaciones, (\d+) carpetas y (\d+) bóvedas actuales por (\d+) aplicaciones, (\d+) carpetas y (\d+) bóvedas\. También se restaurarán (\d+) eventos\.$");
        if (match.Success) return $"The current {match.Groups[1].Value} applications, {match.Groups[2].Value} folders, and {match.Groups[3].Value} vaults will be replaced by {match.Groups[4].Value} applications, {match.Groups[5].Value} folders, and {match.Groups[6].Value} vaults. {match.Groups[7].Value} events will also be restored.";
        match = Regex.Match(value, @"^(\d+) reglas apuntan a archivos que no existen actualmente; permanecerán en la lista para poder corregirlas o instalarlas después\.$");
        if (match.Success) return $"{match.Groups[1].Value} rules point to files that do not currently exist; they will remain in the list so they can be corrected or installed later.";
        match = Regex.Match(value, @"^Se excluirán (\d+) reglas que intentan proteger componentes de ProtectedApp o procesos esenciales de Windows\.$");
        if (match.Success) return $"{match.Groups[1].Value} rules attempting to protect ProtectedApp components or essential Windows processes will be excluded.";
        match = Regex.Match(value, @"^Se omitirán (\d+) carpetas que ya no existen para evitar reglas imposibles de restaurar\.$");
        if (match.Success) return $"{match.Groups[1].Value} folders that no longer exist will be skipped to avoid impossible rules to restore.";
        match = Regex.Match(value, @"^Se omitirán (\d+) referencias a bóvedas cuyo contenedor cifrado ya no existe en esa ruta\.$");
        if (match.Success) return $"{match.Groups[1].Value} vault references whose encrypted container no longer exists at that path will be skipped.";
        match = Regex.Match(value, @"^ · última copia (.+)$");
        if (match.Success) return $" · last backup {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Se eliminarán hasta (\d+) copias antiguas\. Se conservarán las (\d+) versiones más recientes de cada bóveda\.$");
        if (match.Success) return $"Up to {match.Groups[1].Value} old backups will be removed. The {match.Groups[2].Value} most recent versions of each vault will be kept.";
        match = Regex.Match(value, @"^No se pudo abrir el selector de copias de bóvedas: (.+)$");
        if (match.Success) return $"Could not open the vault backup picker: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Actualización manual autorizada: (.+) → (.+); paquete (.+)$");
        if (match.Success) return $"Manual update authorized: {match.Groups[1].Value} → {match.Groups[2].Value}; package {match.Groups[3].Value}";
        match = Regex.Match(value, @"^Iniciando actualización (.+)…$");
        if (match.Success) return $"Starting update {match.Groups[1].Value}…";
        match = Regex.Match(value, @"^Retiradas (\d+) regla\(s\) heredada\(s\) de carpetas NTFS; usa bóvedas cifradas para proteger su contenido$");
        if (match.Success) return $"Removed {match.Groups[1].Value} inherited NTFS folder rule(s); use encrypted vaults to protect their contents";
        match = Regex.Match(value, @"^Máximo: (.+) min$");
        if (match.Success) return $"Maximum: {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^Introduce la contraseña para recuperar el trabajo pendiente\.\r?\n(.+)$");
        if (match.Success) return $"Enter the password to recover pending work.{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Los archivos se han guardado en:\r?\n(.+)\r?\n\r?\nLa carpeta de trabajo se eliminó después de verificar el contenedor cifrado\.$");
        if (match.Success) return $"Files were saved to:{Environment.NewLine}{match.Groups[1].Value}{Environment.NewLine}{Environment.NewLine}The working folder was removed after verifying the encrypted container.";
        match = Regex.Match(value, @"^No se pudo recuperar (.+)\. La carpeta de trabajo continúa en:\r?\n(.+)\r?\n\r?\n(.+)$");
        if (match.Success) return $"Could not recover {match.Groups[1].Value}. The working folder remains at:{Environment.NewLine}{match.Groups[2].Value}{Environment.NewLine}{Environment.NewLine}{match.Groups[3].Value}";
        match = Regex.Match(value, @"^Se quitarán de la lista (\d+) referencia\(s\) cuyo archivo cifrado y copia de recuperación ya no existen\. No se eliminará ningún archivo\.(.*)$", RegexOptions.Singleline);
        if (match.Success) return $"{match.Groups[1].Value} reference(s) whose encrypted file and recovery backup no longer exist will be removed from the list. No files will be deleted.{match.Groups[2].Value}";
        match = Regex.Match(value, @"^Se eliminará permanentemente esta copia cifrada, sin modificar el contenedor principal:\r?\n(.+)$");
        if (match.Success) return $"This encrypted backup will be permanently deleted without modifying the primary container:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^La copia se verificó y se restauró correctamente\. El contenedor sustituido se conserva en:\r?\n(.+)$");
        if (match.Success) return $"The backup was verified and restored successfully. The replaced container is kept at:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^La versión se restauró correctamente\. El contenedor sustituido se conserva en:\r?\n(.+)$");
        if (match.Success) return $"The version was restored successfully. The replaced container is kept at:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^La copia anterior se restauró correctamente\. El contenedor sustituido se conserva en:\r?\n(.+)$");
        if (match.Success) return $"The previous backup was restored successfully. The replaced container is kept at:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Bóveda recuperada tras comprobar la integridad; el contenedor sustituido se conserva en (.+)$");
        if (match.Success) return $"Vault recovered after integrity checking; the replaced container is kept at {match.Groups[1].Value}";
        match = Regex.Match(value, @"^No se pudo eliminar la carpeta original\. Revísala y elimínala manualmente cuando proceda\.\r?\n\r?\n(.+)$");
        if (match.Success) return $"Could not remove the original folder. Review it and remove it manually when appropriate.{Environment.NewLine}{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Bóveda cifrada creada desde (.+)$");
        if (match.Success) return $"Encrypted vault created from {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Protección activada en lote para '(.+)' \((\d+) aplicaciones\)$");
        if (match.Success) return $"Batch protection enabled for '{match.Groups[1].Value}' ({match.Groups[2].Value} applications)";
        match = Regex.Match(value, @"^Protección desactivada en lote para '(.+)' \((\d+) aplicaciones\)$");
        if (match.Success) return $"Batch protection disabled for '{match.Groups[1].Value}' ({match.Groups[2].Value} applications)";
        match = Regex.Match(value, @"^\r?\n• y (\d+) trabajo\(s\) más$");
        if (match.Success) return $"{Environment.NewLine}• and {match.Groups[1].Value} more task(s)";
        match = Regex.Match(value, @"^\r?\n• y (\d+) más$");
        if (match.Success) return $"{Environment.NewLine}• and {match.Groups[1].Value} more";
        match = Regex.Match(value, @"^Revísalas para guardar sus cambios o descartarlas de forma segura\.(.*)$", RegexOptions.Singleline);
        if (match.Success) return $"Review them to save their changes or discard them safely.{match.Groups[1].Value}";
        match = Regex.Match(value, @"^(.+)\r?\n\r?\nNo hay una copia cifrada anterior válida para recuperarla automáticamente\.$", RegexOptions.Singleline);
        if (match.Success) return $"{match.Groups[1].Value}{Environment.NewLine}{Environment.NewLine}There is no valid previous encrypted backup to recover automatically.";
        return value;
    }

    private static string TranslateDynamicEnglish(string value)
    {
        var match = Regex.Match(value, @"^Global shortcut: (.+)$");
        if (match.Success) return $"Atajo global: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not prepare the original folder for deletion\. No files were deleted\. Close applications using it and check its permissions:\r?\n(.+)$");
        if (match.Success) return $"No se pudo preparar la carpeta original para eliminarla. No se eliminó ningún archivo. Cierra las aplicaciones que la usen y revisa sus permisos:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^The original folder was moved to a cleanup location before its contents were deleted, but Windows did not complete the operation\. The encrypted vault has already been verified\. Review and remove the remaining folder manually:\r?\n(.+)$");
        if (match.Success) return $"La carpeta original se movió a una ubicación de limpieza antes de borrar su contenido, pero Windows no terminó la operación. La bóveda cifrada ya fue verificada. Revisa y elimina manualmente la carpeta restante:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not remove the original folder because Windows denied access\. Close applications using it and check its permissions before removing it manually:\r?\n(.+)$");
        if (match.Success) return $"No se pudo eliminar la carpeta original porque Windows denegó el acceso. Cierra las aplicaciones que la usen y revisa sus permisos antes de eliminarla manualmente:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^(\d+) protected folder\(s\) available\.$");
        if (match.Success) return $"{match.Groups[1].Value} carpetas protegidas disponibles.";
        match = Regex.Match(value, @"^(\d+) protected folder\(s\) no longer exist or are unavailable\.$");
        if (match.Success) return $"{match.Groups[1].Value} carpetas protegidas ya no existen o no están disponibles.";
        match = Regex.Match(value, @"^(\d+) \.pavault file\(s\) are unavailable\.$");
        if (match.Success) return $"{match.Groups[1].Value} archivos .pavault no están disponibles.";
        match = Regex.Match(value, @"^(\d+) vault\(s\) have pending changes to check before opening\.$");
        if (match.Success) return $"{match.Groups[1].Value} bóvedas tienen cambios pendientes de comprobar antes de abrirse.";
        match = Regex.Match(value, @"^(\d+) vault\(s\) available\.$");
        if (match.Success) return $"{match.Groups[1].Value} bóvedas disponibles.";
        match = Regex.Match(value, @"^(\d+) vault\(s\) are marked as open, but their drive does not respond: (.+)\.$");
        if (match.Success) return $"{match.Groups[1].Value} bóveda(s) figuran como abiertas, pero su unidad no responde: {match.Groups[2].Value}.";
        match = Regex.Match(value, @"^(\d+) drive\(s\) do not expose the PAVLT003 file system: (.+)\.$");
        if (match.Success) return $"{match.Groups[1].Value} unidad(es) no exponen el sistema de archivos PAVLT003: {match.Groups[2].Value}.";
        match = Regex.Match(value, @"^(\d+) vault\(s\) open and accessible through PAVLT003\.$");
        if (match.Success) return $"{match.Groups[1].Value} bóveda(s) abierta(s) y accesible(s) mediante PAVLT003.";
        match = Regex.Match(value, @"^(\d+) vault\(s\) retain a journal from a previous session\. Open and lock them again to consolidate or review the changes\.$");
        if (match.Success) return $"{match.Groups[1].Value} bóveda(s) conservan un diario de una sesión anterior. Ábrelas y vuelve a bloquearlas para consolidar o revisar los cambios.";
        match = Regex.Match(value, @"^No recovery is pending\. (\d+) vault\(s\) have a previous encrypted backup available\.$");
        if (match.Success) return $"No hay recuperación pendiente. {match.Groups[1].Value} bóveda(s) tienen una copia cifrada anterior disponible.";
        match = Regex.Match(value, @"^Encrypted vault created from (.+)$");
        if (match.Success) return $"Bóveda cifrada creada desde {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Folder unlocked for (\d+) min$");
        if (match.Success) return $"Carpeta desbloqueada durante {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^Automatic panel lock set to (\d+) min$");
        if (match.Success) return $"Bloqueo automático del panel configurado en {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^Panel locked automatically after (\d+) min of inactivity$");
        if (match.Success) return $"Panel bloqueado automáticamente tras {match.Groups[1].Value} min sin actividad";
        match = Regex.Match(value, @"^Vault restored from the scheduled backup of (.+)$");
        if (match.Success) return $"Bóveda restaurada desde la copia programada del {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Scheduled version checked successfully \((.+)\)$");
        if (match.Success) return $"Versión programada comprobada correctamente ({match.Groups[1].Value})";
        match = Regex.Match(value, @"^(\d+) pending vault recovery task\(s\) detected$");
        if (match.Success) return $"Detectados {match.Groups[1].Value} trabajo(s) de bóveda pendientes de recuperación";
        match = Regex.Match(value, @"^Removed (\d+) vault reference\(s\) without an available container$");
        if (match.Success) return $"Eliminadas {match.Groups[1].Value} referencia(s) de bóvedas sin contenedor disponible";
        match = Regex.Match(value, @"^Activity log exported with (\d+) event\(s\)$");
        if (match.Success) return $"Registro de actividad exportado con {match.Groups[1].Value} eventos";
        match = Regex.Match(value, @"^Backup restored with (\d+) applications, (\d+) folders, and (\d+) vaults$");
        if (match.Success) return $"Copia de seguridad restaurada con {match.Groups[1].Value} aplicaciones, {match.Groups[2].Value} carpetas y {match.Groups[3].Value} bóvedas";
        match = Regex.Match(value, @"^Encrypted backup exported with (\d+) applications and (\d+) folders$");
        if (match.Success) return $"Copia de seguridad cifrada exportada con {match.Groups[1].Value} aplicaciones y {match.Groups[2].Value} carpetas";
        match = Regex.Match(value, @"^Automatic closing extended by (\d+) min$");
        if (match.Success) return $"Cierre automático ampliado {match.Groups[1].Value} min";
        match = Regex.Match(value, @"^(\d+) component\(s\) still require review\.$");
        if (match.Success) return $"Quedan {match.Groups[1].Value} componentes que requieren revisión.";
        match = Regex.Match(value, @"^(\d+) visible event\(s\) were exported\. The current search and filter were retained\.$");
        if (match.Success) return $"Se han exportado {match.Groups[1].Value} eventos visibles. La búsqueda y el filtro actuales se han respetado.";
        match = Regex.Match(value, @"^The (\d+) events stored on this computer will be permanently deleted\.$");
        if (match.Success) return $"Se eliminarán permanentemente los {match.Groups[1].Value} eventos guardados en este equipo.";
        match = Regex.Match(value, @"^(.+) can be opened again without a password\.$");
        if (match.Success) return $"{match.Groups[1].Value} podrá volver a abrirse sin contraseña.";
        match = Regex.Match(value, @"^Only access to (.+) will be revoked and its open processes will be closed\. Unsaved work may be lost\.$");
        if (match.Success) return $"Se revocará únicamente el acceso de {match.Groups[1].Value} y se cerrarán sus procesos abiertos. El trabajo no guardado podría perderse.";
        match = Regex.Match(value, @"^Batch protection enabled for '(.+)' \((\d+) applications\)$");
        if (match.Success) return $"Protección activada en lote para '{match.Groups[1].Value}' ({match.Groups[2].Value} aplicaciones)";
        match = Regex.Match(value, @"^Batch protection disabled for '(.+)' \((\d+) applications\)$");
        if (match.Success) return $"Protección desactivada en lote para '{match.Groups[1].Value}' ({match.Groups[2].Value} aplicaciones)";
        match = Regex.Match(value, @"^Immediate lock applied; (\d+) protected process\(es\) ended, (\d+) folder\(s\), and (\d+) vault\(s\) locked$");
        if (match.Success) return $"Bloqueo inmediato aplicado; {match.Groups[1].Value} procesos protegidos finalizados, {match.Groups[2].Value} carpetas y {match.Groups[3].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Applied; (\d+) protected process\(es\) ended, (\d+) folder\(s\), and (\d+) vault\(s\) locked$");
        if (match.Success) return $"Aplicado; {match.Groups[1].Value} procesos protegidos finalizados, {match.Groups[2].Value} carpetas y {match.Groups[3].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Shortcut applied; (\d+) protected process\(es\) ended, (\d+) vault\(s\) locked$");
        if (match.Success) return $"Atajo aplicado; {match.Groups[1].Value} procesos protegidos finalizados, {match.Groups[2].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Shortcut applied; (\d+) application\(s\) closed normally, (\d+) forcibly closed, (\d+) vault\(s\) locked$");
        if (match.Success) return $"Atajo aplicado; {match.Groups[1].Value} aplicaciones cerradas normalmente, {match.Groups[2].Value} cierres forzados, {match.Groups[3].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Immediate lock applied; (\d+) application\(s\) closed normally, (\d+) forcibly closed, (\d+) folder\(s\) and (\d+) vault\(s\) locked$");
        if (match.Success) return $"Bloqueo inmediato aplicado; {match.Groups[1].Value} aplicaciones cerradas normalmente, {match.Groups[2].Value} cierres forzados, {match.Groups[3].Value} carpetas y {match.Groups[4].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Applied; (\d+) application\(s\) closed normally, (\d+) forcibly closed, (\d+) folder\(s\) and (\d+) vault\(s\) locked$");
        if (match.Success) return $"Aplicado; {match.Groups[1].Value} aplicaciones cerradas normalmente, {match.Groups[2].Value} cierres forzados, {match.Groups[3].Value} carpetas y {match.Groups[4].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Shortcut applied; close requests sent to (\d+) protected application\(s\), (\d+) vault\(s\) locked$");
        if (match.Success) return $"Atajo aplicado; {match.Groups[1].Value} solicitudes de cierre enviadas a aplicaciones protegidas, {match.Groups[2].Value} bóvedas bloqueadas";
        match = Regex.Match(value, @"^Preferred vault drive letter set to ([A-Z]):$");
        if (match.Success) return $"Letra preferida para bóvedas configurada en {match.Groups[1].Value}:";
        match = Regex.Match(value, @"^Vault backup cleanup: (\d+) deleted, (.+) freed$");
        if (match.Success) return $"Limpieza de copias de bóvedas: {match.Groups[1].Value} eliminadas, {match.Groups[2].Value} liberados";
        match = Regex.Match(value, @"^(\d+) vault backup\(s\) created\.$");
        if (match.Success) return $"Se crearon {match.Groups[1].Value} copias de bóveda.";
        match = Regex.Match(value, @"^Installed version (.+)$");
        if (match.Success) return $"Versión instalada {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not unmount the virtual drive: (.+)$");
        if (match.Success) return $"No se pudo desmontar la unidad virtual: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not automatically restore the previous file: (.+)$");
        if (match.Success) return $"No se pudo restaurar automáticamente el archivo anterior: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not start: (.+)$");
        if (match.Success) return $"No se pudo iniciar: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Guardian rejected the launch: (.+)$");
        if (match.Success) return $"Guardian rechazó el inicio: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not review vault recovery: (.+)$");
        if (match.Success) return $"No se pudo revisar la recuperación de bóvedas: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Recovery stopped by the preliminary check: (.+)$");
        if (match.Success) return $"Recuperación detenida por la comprobación previa: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not restore scheduled version: (.+)$");
        if (match.Success) return $"No se pudo restaurar una versión programada: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Recovery failed after integrity checking: (.+)$");
        if (match.Success) return $"Falló la recuperación después de comprobar la integridad: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Automatic recovery from the previous backup failed: (.+)$");
        if (match.Success) return $"Falló la recuperación automática de la copia anterior: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Vault restored and imported; the damaged container was preserved at (.+)$");
        if (match.Success) return $"Bóveda restaurada e importada; el contenedor dañado se conservó en {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Previous backup restored while opening; the replaced container was preserved at (.+)$");
        if (match.Success) return $"Copia anterior restaurada al abrir; el contenedor sustituido se conservó en {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Container recovered from the previous backup; the replaced file was preserved at (.+)$");
        if (match.Success) return $"Contenedor recuperado desde la copia anterior; el archivo sustituido se conservó en {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Vault permanently deleted along with (\d+) encrypted backup\(s\)$");
        if (match.Success) return $"Bóveda eliminada permanentemente junto con {match.Groups[1].Value} copia(s) cifrada(s)";
        match = Regex.Match(value, @"^(\d+) vault backup\(s\) requiring recovery detected$");
        if (match.Success) return $"Detectadas {match.Groups[1].Value} copia(s) de bóveda que requieren recuperación";
        match = Regex.Match(value, @"^(\d+) pending vault recovery task\(s\) detected$");
        if (match.Success) return $"Detectados {match.Groups[1].Value} trabajo(s) de bóveda pendientes de recuperación";
        match = Regex.Match(value, @"^Vault locked automatically(.*)$");
        if (match.Success) return $"Bóveda bloqueada automáticamente{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Safe viewing closed automatically(.*)$");
        if (match.Success) return $"Consulta segura cerrada automáticamente{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not automatically unmount the virtual drive (.+)$");
        if (match.Success) return $"No se pudo desmontar automáticamente la unidad virtual {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Could not apply automatic lock (.+); the working folder was preserved$");
        if (match.Success) return $"No se pudo aplicar el bloqueo automático {match.Groups[1].Value}; la carpeta de trabajo se conservó";
        match = Regex.Match(value, @"^The vault is open at:\r?\n(.+)$");
        if (match.Success) return $"La bóveda está abierta en:{Environment.NewLine}{match.Groups[1].Value}";
        match = Regex.Match(value, @"^(\d+) vault\(s\) automatically saved and locked because of (.+)$");
        if (match.Success) return $"{match.Groups[1].Value} bóveda(s) guardadas y bloqueadas automáticamente por {match.Groups[2].Value}";
        match = Regex.Match(value, @"^Could not save and lock the vault during (.+); its working folder was preserved$");
        if (match.Success) return $"No se pudo guardar y bloquear la bóveda durante {match.Groups[1].Value}; se conservó su carpeta de trabajo";
        match = Regex.Match(value, @"^Error protecting vaults after a Windows event: (.+)$");
        if (match.Success) return $"Error al proteger las bóvedas ante un evento de Windows: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^(.+) · (\d+) most recent event\(s\)$");
        if (match.Success) return $"{match.Groups[1].Value} · {match.Groups[2].Value} evento(s) más recientes";
        match = Regex.Match(value, @"^There are (\d+) vaults whose primary container is missing or damaged\. ProtectedApp found previous encrypted backups to recover them\.$");
        if (match.Success) return $"Hay {match.Groups[1].Value} bóvedas cuyo contenedor principal está ausente o dañado. ProtectedApp ha encontrado copias cifradas anteriores para recuperarlas.";
        match = Regex.Match(value, @"^There are (\d+) vault\(s\) with a missing or damaged primary container; (\d+) have a structurally recoverable backup and the rest require manual review\.$");
        if (match.Success) return $"Hay {match.Groups[1].Value} bóveda(s) con el contenedor principal ausente o dañado; {match.Groups[2].Value} tienen una copia con estructura recuperable y las demás requieren revisión manual.";
        match = Regex.Match(value, @"^(.+) has changes not yet consolidated into the encrypted file\. Unmounting it will save them safely before closing the drive\.$");
        if (match.Success) return $"{match.Groups[1].Value} contiene cambios que todavía no se han consolidado en el archivo cifrado. Al desmontarla se guardarán de forma segura antes de cerrar la unidad.";
        match = Regex.Match(value, @"^The container for (.+) will be permanently deleted\. This action cannot be undone\.$");
        if (match.Success) return $"Se eliminará permanentemente el contenedor de {match.Groups[1].Value}. Esta acción no se puede deshacer.";
        match = Regex.Match(value, @"^Authorize permanent deletion of (.+)$");
        if (match.Success) return $"Autorizar eliminación permanente de {match.Groups[1].Value}";
        match = Regex.Match(value, @"^The backup from (.+) will be permanently deleted\. This action cannot be undone\.$");
        if (match.Success) return $"Se eliminará la copia del {match.Groups[1].Value}. Esta acción no se puede deshacer.";
        match = Regex.Match(value, @"^The primary container for (.+) is missing or damaged, but a previous encrypted backup can be recovered\.$");
        if (match.Success) return $"El contenedor principal de {match.Groups[1].Value} está ausente o dañado, pero existe una copia cifrada anterior que puede recuperarse.";
        match = Regex.Match(value, @"^(.+) for (.+)\. The backup can be restored from the vaults section\.$");
        if (match.Success) return $"{match.Groups[1].Value} para {match.Groups[2].Value}. La copia puede restaurarse desde la sección de bóvedas.";
        match = Regex.Match(value, @"^Windows started (.+), but at least one vault could not be saved and locked\. (.+)$", RegexOptions.Singleline);
        if (match.Success) return $"Windows inició {match.Groups[1].Value}, pero no se pudo guardar y bloquear al menos una bóveda. {match.Groups[2].Value}";
        match = Regex.Match(value, @"^ProtectedApp encountered an error protecting vaults after a Windows event: (.+)$");
        if (match.Success) return $"ProtectedApp encontró un error al proteger las bóvedas tras un evento de Windows: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^(.+) will close automatically in less than one minute\.$");
        if (match.Success) return $"{match.Groups[1].Value} se cerrará automáticamente en menos de un minuto.";
        match = Regex.Match(value, @"^Could not extend automatic closing: (.+)$");
        if (match.Success) return $"No se pudo ampliar el cierre automático: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Launch denied: (.+)$");
        if (match.Success) return $"Ejecución denegada: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Tampering detected: (.+)$");
        if (match.Success) return $"Manipulación detectada: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Guardian installer finished with code (.+)\. Try again; the next attempt will show details if Windows rejects a step\.$");
        if (match.Success) return $"El instalador de Guardian terminó con el código {match.Groups[1].Value}. Vuelve a intentarlo; el próximo intento mostrará el detalle si Windows rechaza algún paso.";
        return value;
    }

    public static void ApplyTo(DependencyObject root)
    {
        if (root is null) return;
        ApplyElement(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++) ApplyTo(VisualTreeHelper.GetChild(root, index));
    }

    private static void ApplyElement(DependencyObject element)
    {
        var automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName))
            AutomationProperties.SetName(element, T(element, "AutomationName", automationName));
        var attachedToolTip = ToolTipService.GetToolTip(element);
        if (attachedToolTip is string toolTipText)
            ToolTipService.SetToolTip(element, T(element, "ToolTip", toolTipText));

        switch (element)
        {
            case ContentDialog dialog:
                if (dialog.Title is string title) dialog.Title = T(dialog, "Title", title);
                if (dialog.Content is string body) dialog.Content = T(dialog, "Content", body);
                dialog.PrimaryButtonText = T(dialog, "PrimaryButtonText", dialog.PrimaryButtonText);
                dialog.SecondaryButtonText = T(dialog, "SecondaryButtonText", dialog.SecondaryButtonText);
                dialog.CloseButtonText = T(dialog, "CloseButtonText", dialog.CloseButtonText);
                if (dialog.Content is DependencyObject dialogContent) ApplyTo(dialogContent);
                break;
            case TextBlock textBlock:
                textBlock.Text = T(textBlock, "Text", textBlock.Text);
                break;
            case Button button when button.Content is string content:
                button.Content = T(button, "Content", content);
                break;
            case ComboBoxItem item when item.Content is string content:
                item.Content = T(item, "Content", content);
                break;
            case MenuFlyoutItem menuItem:
                menuItem.Text = T(menuItem, "Text", menuItem.Text);
                break;
            case ToggleSwitch toggleSwitch:
                if (toggleSwitch.Header is string toggleHeader) toggleSwitch.Header = T(toggleSwitch, "Header", toggleHeader);
                if (toggleSwitch.OnContent is string onContent) toggleSwitch.OnContent = T(toggleSwitch, "OnContent", onContent);
                else toggleSwitch.OnContent = T(toggleSwitch, "OnContent", "Activado");
                if (toggleSwitch.OffContent is string offContent) toggleSwitch.OffContent = T(toggleSwitch, "OffContent", offContent);
                else toggleSwitch.OffContent = T(toggleSwitch, "OffContent", "Desactivado");
                break;
            case ComboBox comboBox:
                if (comboBox.Header is string comboHeader) comboBox.Header = T(comboBox, "Header", comboHeader);
                foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
                    if (item.Content is string content) item.Content = T(item, "Content", content);
                break;
            case TextBox textBox when textBox.Header is string textBoxHeader:
                textBox.Header = T(textBox, "Header", textBoxHeader);
                textBox.PlaceholderText = T(textBox, "PlaceholderText", textBox.PlaceholderText);
                break;
            case NumberBox numberBox when numberBox.Header is string numberBoxHeader:
                numberBox.Header = T(numberBox, "Header", numberBoxHeader);
                break;
            case TimePicker timePicker when timePicker.Header is string timePickerHeader:
                timePicker.Header = T(timePicker, "Header", timePickerHeader);
                break;
            case TextBox textBox:
                textBox.PlaceholderText = T(textBox, "PlaceholderText", textBox.PlaceholderText);
                break;
            case PasswordBox passwordBox:
                passwordBox.PlaceholderText = T(passwordBox, "PlaceholderText", passwordBox.PlaceholderText);
                if (passwordBox.Header is string passwordHeader) passwordBox.Header = T(passwordBox, "Header", passwordHeader);
                break;
        }

        if (element is FrameworkElement frameworkElement)
        {
            if (ToolTipService.GetToolTip(frameworkElement) is string toolTip)
                ToolTipService.SetToolTip(frameworkElement, T(frameworkElement, "ToolTip", toolTip));
            var name = AutomationProperties.GetName(frameworkElement);
            if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(frameworkElement, T(frameworkElement, "AutomationName", name));
            TranslateFlyout(frameworkElement.ContextFlyout);
            if (frameworkElement is Button button) TranslateFlyout(button.Flyout);
        }
    }

    public static void TranslateFlyout(FlyoutBase? flyout)
    {
        if (flyout is not MenuFlyout menu) return;
        TranslateMenuItems(menu.Items);
    }

    private static void TranslateMenuItems(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item is MenuFlyoutItem command) command.Text = T(command, "Text", command.Text);
            if (item is MenuFlyoutSubItem subMenu)
            {
                subMenu.Text = T(subMenu, "Text", subMenu.Text);
                TranslateMenuItems(subMenu.Items);
            }
        }
    }
}

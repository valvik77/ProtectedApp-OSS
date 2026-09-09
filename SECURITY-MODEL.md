# Modelo de seguridad

## Propósito

ProtectedApp ayuda a una persona que usa Windows a proteger aplicaciones de
escritorio seleccionadas mediante contraseña y a conservar archivos en bóvedas
cifradas. Guardian aplica las reglas aunque el panel de gestión esté oculto.

## Límites explícitos

ProtectedApp no es un antivirus, un escáner de vulnerabilidades ni una
herramienta para identificar, explotar o eludir controles de seguridad. Tampoco
es una frontera frente a un administrador local, `SYSTEM`, malware que ya se
ejecute con privilegios equivalentes o acceso físico al equipo. Complementa, no
sustituye, BitLocker, Secure Boot, cuentas de Windows, copias de seguridad y
políticas App Control/WDAC.

La protección de aplicaciones controla el acceso desde una sesión normal de
Windows. Puede cerrar procesos protegidos al bloquearse y, por ello, una persona
puede perder trabajo que la aplicación externa todavía no haya guardado. El
producto muestra avisos antes de las acciones de bloqueo y ofrece desinstalación
mediante el instalador oficial.

## Cambios en el sistema

La instalación puede registrar el servicio Windows Guardian, una tarea de
recuperación que se ejecuta como `SYSTEM`, integraciones opcionales del
Explorador y el runtime Dokany necesario para las unidades virtuales de las
bóvedas. Estos componentes, sus rutas y sus permisos se comprueban mediante los
diagnósticos de la aplicación. La desinstalación oficial elimina los componentes
propios de ProtectedApp; Dokany se conserva cuando puede ser compartido por
otros programas.

## Datos y red

Las bóvedas usan cifrado autenticado; la contraseña de una bóveda no convierte
el equipo en un entorno aislado ni recupera datos que hayan sido expuestos antes
de bloquearla. El estado local de la aplicación y los secretos opcionales del
webhook reciben protección de Windows/DPAPI según el componente que los use.

ProtectedApp no incorpora telemetría. Las comunicaciones de red posibles son el
webhook HTTPS de alertas de manipulación configurado expresamente por la persona
operadora y las consultas OCSP/CRL que Windows puede realizar al comprobar la
revocación de la firma Authenticode de un instalador elegido manualmente. La
aplicación no descarga ni instala actualizaciones automáticamente.

## Integridad de versiones

Las versiones oficiales se construyen desde GitHub Actions, pasan compilación,
pruebas, auditorías de dependencias y de repositorio, y se publican con hashes.
Las claves de firma pública no se almacenan en el repositorio ni en los equipos
de desarrollo. Consulta la [Code signing policy](CODE-SIGNING-POLICY.md) y
[SIGNING.md](SIGNING.md) para el proceso de firma y aprobación.

## Informar de problemas

Comunica vulnerabilidades conforme a [SECURITY.md](SECURITY.md), no mediante
incidencias públicas con datos explotables o secretos.

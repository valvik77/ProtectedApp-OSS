# Code signing policy

## Estado y proveedor

ProtectedApp prevé solicitar la firma de sus versiones públicas al programa de código
abierto de SignPath Foundation. Cuando la solicitud sea aprobada, las versiones
oficiales llevarán esta declaración: **Free code signing provided by
SignPath.io, certificate by SignPath Foundation.**

Hasta que esa aprobación exista, ningún binario firmado con el certificado de
desarrollo local es una versión oficial ni debe distribuirse como tal.

## Responsabilidades

- **Autor, mantenedor, committer y revisor:** [@valvik77](https://github.com/valvik77).
  Puede modificar el código y revisa las contribuciones externas.
- **Aprobador de firma:** [@valvik77](https://github.com/valvik77). Cada solicitud
  de firma de una versión requiere su aprobación manual.
- Las contribuciones de personas sin permiso de escritura se realizan mediante
  pull request y se revisan antes de integrarse en `main`.

La rama `main` exige pull request y CI; las versiones solo se generan desde una
revisión etiquetada y revisada. Los artefactos deben proceder de GitHub-hosted
Actions, superar compilación, pruebas y auditoría de dependencias, y conservar
su procedencia verificable.

## Alcance de la firma

Solo se firman binarios propios construidos desde este repositorio oficial. No
se firman binarios de terceros con el certificado del proyecto. Las versiones
publicadas incluyen las notas de versión, hashes SHA-256 y, cuando esté
disponible, la comprobación Authenticode correspondiente.

Las claves privadas no se guardan en Git, en artefactos de CI ni en equipos de
desarrollo. La configuración de firma, el cambio de certificado y la validación
de actualizaciones están documentados en [SIGNING.md](SIGNING.md).

## Privacidad y comunicaciones de red

ProtectedApp no transmite información a sistemas en red salvo que la persona
que lo instala u opera configure expresamente el webhook opcional de alertas de
manipulación. Ese webhook HTTPS envía únicamente los datos descritos en el
README. Las actualizaciones se seleccionan manualmente desde un archivo local;
la aplicación no descarga ni instala actualizaciones automáticamente.

## Comunicación de problemas

Las vulnerabilidades se comunican de forma privada conforme a
[SECURITY.md](SECURITY.md). No se firman correcciones ni versiones que no hayan
pasado las comprobaciones de seguridad y revisión aplicables.

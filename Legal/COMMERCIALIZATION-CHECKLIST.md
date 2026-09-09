# Lista de preparación para distribuir ProtectedApp

Este documento es una lista operativa, no asesoramiento jurídico. Está adaptado a la distribución gratuita de ProtectedApp y debe revisarse con un profesional si se incorporan pagos, servicios en línea o un modelo comercial distinto.

## Antes de una distribución pública amplia

- Publicar una identidad del responsable y un canal de contacto o soporte verificable.
- Mantener visible que ProtectedApp es gratuito para uso personal y profesional, pero propietario.
- Publicar una política de privacidad si la web, el soporte, las donaciones, la telemetría o un endpoint webhook recogen datos personales. ProtectedApp no envía datos al responsable por defecto.
- Obtener un certificado público de firma de código antes de una entrega general. El certificado de desarrollo actual solo sirve para equipos de prueba que confíen expresamente en él.
- Publicar sumas SHA-256 de los instaladores oficiales y un historial de versiones.

## Licencias y propiedad intelectual

- Mantener Legal/THIRD-PARTY-NOTICES.txt y conservar en cada instalador los textos generados bajo Legal/Licenses.
- No modificar Dokany sin separar y publicar el código fuente correspondiente bajo LGPL. Mantener el enlace dinámico y el runtime sustituible.
- Revisar las licencias al actualizar paquetes. El build bloquea paquetes Windows App SDK que contengan términos Engineering Preview, pero el inventario debe revisarse también manualmente en cada versión oficial.
- Comprobar disponibilidad de la marca y dominios de ProtectedApp antes de invertir en distribución.

## Seguridad, soporte y cumplimiento futuro

- Mantener un canal para comunicar vulnerabilidades, un procedimiento de respuesta y un historial de versiones y actualizaciones de seguridad.
- Evitar promesas absolutas como «imposible de vulnerar». Documentar que un administrador/SYSTEM queda fuera de la frontera de confianza y que las bóvedas no sustituyen copias de seguridad.
- Preparar SBOM, evaluación de riesgos, gestión de vulnerabilidades, documentación técnica y proceso de actualizaciones para el Reglamento de Ciberresiliencia de la UE (CRA). Sus obligaciones de notificación empiezan parcialmente el 11 de septiembre de 2026 y el reglamento se aplica con carácter general desde el 11 de diciembre de 2027.

## Donaciones o pagos futuros

- Las donaciones voluntarias no deben condicionar el acceso ni las funciones de la versión gratuita.
- Si se añade PayPal u otra pasarela, publicar identidad del receptor, política de privacidad, tratamiento fiscal y condiciones de reembolso cuando procedan.
- Si en el futuro se vende soporte, una edición distinta o un servicio en línea, crear condiciones específicas antes de procesar pagos. La GPL no prohíbe cobrar por soporte o distribución, pero exige conservar sus libertades y obligaciones al redistribuir el programa.

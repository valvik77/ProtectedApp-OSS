# Preparación para publicación open source

Este documento es una lista de verificación previa a hacer público el
repositorio. No sustituye una revisión legal.

## Decisión necesaria antes de publicar

ProtectedApp adopta GPL-3.0-or-later. El titular debe confirmar que tiene
derechos sobre todas las aportaciones antes de hacer público el repositorio.
Las versiones distribuidas modificadas deben ofrecer su código fuente bajo los
mismos términos. Apache-2.0 o MIT no son equivalentes porque permiten
reutilización propietaria.

## Antes de abrir el repositorio

- [x] Excluir claves, certificados privados, variables de entorno, registros y
  resultados de compilación.
- [x] Añadir política de comunicación privada de vulnerabilidades.
- [x] Añadir instrucciones de contribución y verificaciones locales.
- [x] Elegir GPL-3.0-or-later y añadir el aviso de licencia en la raíz.
- [x] Actualizar los avisos de terceros y la documentación de distribución.
- [ ] Revisar manualmente historial Git, Issues, Releases y artefactos antes de
  hacer público el repositorio.
- [ ] Crear el repositorio público, configurar avisos privados de seguridad y
  protección de la rama principal.
- [ ] Solicitar SignPath Foundation si se cumplen sus condiciones, y configurar
  su integración de firma para releases desde una etiqueta protegida.

## Lanzamiento

- [ ] Compilar en Release y ejecutar todas las pruebas.
- [ ] Comprobar dependencias vulnerables y la firma de cada binario distribuido.
- [ ] Publicar hashes SHA-256 y notas de versión.
- [ ] Probar instalación, actualización y desinstalación en una VM limpia.

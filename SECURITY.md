# Política de seguridad

## Versiones compatibles

Solo la versión estable publicada más reciente recibe correcciones de seguridad.

## Comunicar una vulnerabilidad

No publiques vulnerabilidades, pruebas de concepto funcionales ni detalles de
explotación en incidencias públicas. Cuando el repositorio se haga público, usa
la opción **Report a vulnerability** de la pestaña Security de GitHub para crear
un aviso privado al mantenedor.

Incluye una descripción del impacto, los pasos mínimos para reproducirlo, la
versión afectada y, cuando sea posible, una propuesta de corrección. No incluyas
contraseñas, bóvedas reales, claves privadas, tokens ni datos de terceros.

El objetivo es confirmar la recepción, evaluar el impacto, preparar una
corrección y publicar una versión firmada antes de divulgar los detalles.

Si sospechas que la clave del certificado de desarrollo se ha utilizado sin
autorización, indícalo expresamente como **posible compromiso de firma**. Las
personas que lo hayan instalado deberán retirarlo siguiendo
[DEVELOPMENT-CERTIFICATE.md](DEVELOPMENT-CERTIFICATE.md) hasta que se publique
una evaluación.

## Límites del modelo de seguridad

ProtectedApp protege aplicaciones y bóvedas frente a uso accidental o no
autorizado desde una sesión normal de Windows. No es una frontera frente a un
administrador local o `SYSTEM`. Debe complementarse con BitLocker, Secure Boot,
cuentas de Windows, copias de seguridad y, en entornos administrados, App
Control for Business/WDAC.

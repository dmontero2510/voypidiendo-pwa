# VoyPidiendo PWA

Proyecto independiente para la nueva versión multiempresa de VoyPidiendo.

La aplicación existente, su repositorio y su servicio de Render no forman parte de este proyecto.

## Objetivo inicial

- Una única aplicación web progresiva.
- Identificación de la empresa por dominio o subdominio.
- Apariencia, textos y funciones configurables por empresa.
- Conexiones SQL aisladas y disponibles únicamente en el servidor.
- Instalación desde Chrome en Android.
- Actualización centralizada sin recompilar una APK.

## Seguridad

La empresa se obtiene del dominio y se incorpora a la sesión autenticada. Las solicitudes posteriores usan exclusivamente la empresa incluida en el token; el navegador nunca envía ni conoce cadenas de conexión SQL.

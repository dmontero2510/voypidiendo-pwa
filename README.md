# VoyPidiendo PWA multiempresa

Nueva generación independiente de VoyPidiendo. La aplicación original y su despliegue actual no se modifica desde este proyecto.

## Componentes

- **PWA/API:** aplicación única que cada vendedor instala desde el navegador.
- **Superadministración:** panel separado para crear empresas, configurar funciones y administrar sus usuarios.
- **Base central:** guarda empresas, dominios, parámetros y conexiones cifradas.
- **Base de cada cliente:** conserva usuarios, pedidos, clientes, artículos y datos comerciales.

Cada empresa puede tener su propio dominio, nombre, logo, colores, títulos, funciones habilitadas, parámetros de PDF y conexión SQL. El cliente se identifica automáticamente por el dominio utilizado.

## Incorporar una empresa

1. Ingresar al panel de Superadministración.
2. Crear la empresa y asignarle un código único.
3. Cargar su dominio, presentación, parámetros y funciones habilitadas.
4. Guardar su conexión SQL en el campo seguro.
5. Ejecutar **Probar conexión** y **Validar estructura**.
6. Crear o administrar los usuarios propios de esa empresa.
7. Configurar el dominio para que apunte al servidor de VoyPidiendo.

No se cargan empresas ni contraseñas manualmente en `appsettings.json`.

## Instalación de la base de un cliente

El script completo está en `database/000_INSTALACION_CLIENTE_COMPLETA.sql`. Debe revisarse con las vistas y tablas reales del sistema comercial antes de ejecutarlo en producción.

## Desarrollo local

- PWA: `http://localhost:5090/?empresa=0001`
- Superadministración: `http://localhost:5091`

## Instalación en Android

Abrir la dirección HTTPS asignada a la empresa en Chrome y elegir **Instalar aplicación**. Las actualizaciones posteriores se reciben desde el servidor sin reinstalar una APK.

## Publicación

La preparación para Ubuntu, Docker, HTTPS, backups y DNS está documentada en `deploy/README.md`.

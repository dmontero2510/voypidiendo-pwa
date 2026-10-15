# VoyPidiendo PWA multiempresa

Nueva generación independiente de VoyPidiendo. La aplicación original y su despliegue actual no se modifican desde este proyecto.

## Modelo

Una misma aplicación identifica al cliente por el dominio utilizado. Cada empresa tiene:

- nombre, logo, colores, título y subtítulo;
- funciones habilitadas;
- parámetros de pedidos y PDF;
- usuarios en su propia base;
- cadena de conexión SQL exclusiva, almacenada solo en el servidor.

La empresa se incorpora al token al iniciar sesión. Un token emitido para una empresa es rechazado desde el dominio de otra.

## Agregar una empresa

1. Agregar la empresa dentro de `Tenants:Items` en `appsettings.json`.
2. Definir sus dominios en `Hosts`.
3. Asignar un nombre de conexión en `ConnectionStringName`.
4. Crear en el servidor la variable `ConnectionStrings__Tenant_CODIGO` con la conexión SQL real.
5. Configurar el dominio para que apunte al servicio PWA.

Ejemplo:

```json
"empresa2": {
  "Hosts": [ "pedidos.empresa2.com" ],
  "ConnectionStringName": "Tenant_empresa2",
  "Nombre": "Distribuidora Empresa 2",
  "Titulo": "Empresa 2 Pedidos",
  "Subtitulo": "Toma de pedidos",
  "Logo": "/clientes/empresa2/logo.svg",
  "ColorPrincipal": "#1daed8",
  "ColorSecundario": "#10213c",
  "Pedidos": {
    "PercepIIBB": false,
    "Subtotales": true
  },
  "Funciones": {
    "CargaMultiple": true,
    "Seguimiento": true,
    "UbicacionClientes": true,
    "PdfPedidos": true
  }
}
```

La conexión se configura como variable de entorno:

```text
ConnectionStrings__Tenant_empresa2=Server=...;Database=...;User ID=...;Password=...
```

## Desarrollo local

Copiar `src/VoyPidiendo.Api/appsettings.Development.example.json` como `appsettings.Development.json`, completar secretos y ejecutar:

```powershell
dotnet run --project .\src\VoyPidiendo.Api\VoyPidiendo.Api.csproj
```

## Instalación Android

Abrir la dirección HTTPS de la empresa en Chrome y elegir **Instalar aplicación**. Las actualizaciones posteriores se reciben desde el servidor sin reinstalar una APK.

using VoyPidiendo.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<SqlServerRepository>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddSingleton<TenantCatalog>();
builder.Services.AddMemoryCache();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("VoyPidiendo.SuperAdmin");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
var jwtKey = builder.Configuration["Jwt:Key"] ?? builder.Configuration["Security:ApiKey"] ?? "CONFIGURAR-UNA-CLAVE-DE-AL-MENOS-32-CARACTERES";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "VoyPidiendo",
        ValidAudience = "VoyPidiendo",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var tenant = context.RequestServices.GetRequiredService<TenantContext>();
    var catalog = context.RequestServices.GetRequiredService<TenantCatalog>();
    var requestedCode = context.Request.Query["empresa"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(requestedCode))
        context.Response.Cookies.Append("VoyPidiendo.Empresa", requestedCode, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true, MaxAge = TimeSpan.FromDays(30) });
    else
        requestedCode = context.Request.Cookies["VoyPidiendo.Empresa"];
    TenantCatalog.Copy(await catalog.ResolveAsync(context.Request.Host.Host, requestedCode, context.RequestAborted), tenant);
    await next();
});
app.Use(async (context, next) =>
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var request = $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}";
    try
    {
        await next();
        if (context.Request.Path.StartsWithSegments("/api")) AppFileLog.Write("HTTP", $"{request} -> {context.Response.StatusCode} ({watch.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        AppFileLog.Write("ERROR", $"{request} -> ERROR ({watch.ElapsedMilliseconds} ms)", ex);
        throw;
    }
});
app.UseDefaultFiles();
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context =>
    {
        var extension = Path.GetExtension(context.File.Name);
        if (extension is ".html" or ".js" or ".css")
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
    }
});
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var tenant = context.RequestServices.GetRequiredService<TenantContext>();
    var tokenTenant = context.User.FindFirstValue("tenant");
    if (context.User.Identity?.IsAuthenticated == true && !string.Equals(tokenTenant, tenant.Id, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseAuthorization();

app.MapGet("/manifest.webmanifest", (TenantContext tenant) => Results.Json(new
{
    id = $"/?empresa={tenant.Id}",
    name = tenant.Nombre,
    short_name = tenant.Titulo,
    description = tenant.Subtitulo,
    start_url = "/",
    scope = "/",
    display = "standalone",
    background_color = "#eef3f7",
    theme_color = tenant.ColorPrincipal,
    icons = new[] { new { src = tenant.Logo, sizes = "any", type = "image/svg+xml", purpose = "any maskable" } }
}));

app.MapGet("/api/configuracion/empresa", (TenantContext tenant) => Results.Ok(new TenantPublicConfig(
    tenant.Id, tenant.Nombre, tenant.Titulo, tenant.Subtitulo, tenant.Logo,
    tenant.ColorPrincipal, tenant.ColorSecundario, tenant.Funciones,
    tenant.PercepIibb, tenant.Subtotales))).AllowAnonymous();

app.MapPost("/api/auth/login", async (LoginRequest request, AuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Usuario) || string.IsNullOrWhiteSpace(request.Clave)) return Results.BadRequest(new { mensaje = "Ingrese usuario y clave." });
    var result = await auth.LoginAsync(request, ct);
    return result is null ? Results.Unauthorized() : Results.Ok(result);
}).AllowAnonymous();

app.MapPost("/api/auth/bootstrap", async (HttpContext context, BootstrapRequest request, AuthService auth, CancellationToken ct) =>
{
    var expected = app.Configuration["Security:ApiKey"];
    if (string.IsNullOrWhiteSpace(expected) || context.Request.Headers["X-Api-Key"] != expected) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Usuario) || string.IsNullOrWhiteSpace(request.Nombre) || request.Clave.Length < 6)
        return Results.BadRequest(new { mensaje = "Complete usuario, nombre y una clave de al menos 6 caracteres." });
    var result = await auth.BootstrapAsync(request, ct);
    return result.Created ? Results.Ok(new { mensaje = result.Message }) : Results.BadRequest(new { mensaje = result.Message });
}).AllowAnonymous();

app.MapGet("/api/auth/perfil", (ClaimsPrincipal user) => Results.Ok(new
{
    id = user.FindFirstValue(ClaimTypes.NameIdentifier),
    nombre = user.Identity?.Name,
    rol = user.FindFirstValue(ClaimTypes.Role),
    usuario = user.FindFirstValue("usuario"),
    codVend = user.FindFirstValue("cod_vend")
})).RequireAuthorization();

app.MapGet("/api/configuracion/titulo", async (AuthService auth, CancellationToken ct) => Results.Ok(await auth.GetTitleAsync(ct))).AllowAnonymous();
app.MapPut("/api/configuracion/titulo", async (TituloAplicacion value, AuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(value.Titulo) || string.IsNullOrWhiteSpace(value.Subtitulo) || value.Titulo.Length > 50 || value.Subtitulo.Length > 150)
        return Results.BadRequest(new { mensaje = "Complete título y subtítulo respetando sus longitudes máximas." });
    await auth.SaveTitleAsync(value, ct);
    return Results.Ok(value);
}).RequireAuthorization(policy => policy.RequireRole("A"));

app.MapGet("/api/usuarios", async (AuthService auth, CancellationToken ct) => Results.Ok(await auth.ListAsync(ct)))
    .RequireAuthorization(policy => policy.RequireRole("A"));

app.MapPost("/api/usuarios", async (CrearUsuarioRequest request, AuthService auth, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Usuario) || string.IsNullOrWhiteSpace(request.Nombre) || request.Clave.Length < 6)
        return Results.BadRequest(new { mensaje = "Complete usuario, nombre y una clave de al menos 6 caracteres." });
    var result = await auth.CreateUserAsync(request, ct);
    return result.Created ? Results.Ok(new { mensaje = result.Message }) : Results.BadRequest(new { mensaje = result.Message });
}).RequireAuthorization(policy => policy.RequireRole("A"));

app.MapPut("/api/usuarios/{id:int}/habilitado/{enabled:bool}", async (int id, bool enabled, ClaimsPrincipal user, AuthService auth, CancellationToken ct) =>
{
    _ = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var currentId);
    return await auth.SetEnabledAsync(id, enabled, currentId, ct) ? Results.Ok() : Results.BadRequest(new { mensaje = "No se pudo modificar el usuario." });
}).RequireAuthorization(policy => policy.RequireRole("A"));

app.MapPut("/api/usuarios/{id:int}/clave", async (int id, RestablecerClaveRequest request, AuthService auth, CancellationToken ct) =>
{
    if (request.Clave.Length < 6) return Results.BadRequest(new { mensaje = "La clave debe tener al menos 6 caracteres." });
    return await auth.ResetPasswordAsync(id, request.Clave, ct) ? Results.Ok() : Results.NotFound();
}).RequireAuthorization(policy => policy.RequireRole("A"));

app.MapGet("/api/visitas/activa", async (ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    _ = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);
    return Results.Ok(await db.GetActiveVisitAsync(userId, ct));
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapPost("/api/visitas/llegada", async (VisitaRequest request, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    _ = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);
    if (request.Organizacion <= 0 || request.ClienteCodigo <= 0) return Results.BadRequest(new { mensaje = "Seleccione un cliente." });
    var visit = await db.StartVisitAsync(userId, user.FindFirstValue("usuario") ?? "APP", request, ct);
    return visit is null ? Results.BadRequest(new { mensaje = "Ya existe una visita activa." }) : Results.Ok(visit);
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapPost("/api/visitas/salida", async (VisitaSalidaRequest request, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    _ = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);
    var visit = await db.EndVisitAsync(userId, request, ct);
    return visit is null ? Results.BadRequest(new { mensaje = "No hay una visita activa." }) : Results.Ok(visit);
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapGet("/api/visitas", async (DateTime? desde, DateTime? hasta, string? vendedor, string? zona, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetVisitsAsync(desde, hasta, vendedor, zona, ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "P", "Q"));

app.MapGet("/api/clientes/ubicaciones", async (SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetClientLocationsAsync(ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "V", "P", "S", "Q"));

app.MapPut("/api/clientes/ubicaciones", async (ClienteUbicacionRequest request, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    if (request.Organizacion <= 0 || request.ClienteCodigo <= 0 || request.Latitud is < -90 or > 90 || request.Longitud is < -180 or > 180)
        return Results.BadRequest(new { mensaje = "Cliente o ubicación inválidos." });
    await db.SaveClientLocationAsync(request, user.FindFirstValue("usuario") ?? "APP", ct);
    return Results.Ok();
}).RequireAuthorization(policy => policy.RequireRole("A", "V", "P", "S", "Q"));

app.MapGet("/api/sync", async (SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetSyncDataAsync(ct))).RequireAuthorization();

app.MapGet("/api/pedidos/consulta", async (int? desde, int? hasta, string? vendedor, string? cliente, string? zona, int? pagina, int? tamanoPagina, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    if (user.IsInRole("V")) vendedor = user.FindFirstValue("usuario");
    return Results.Ok(await db.GetOrdersAsync(desde, hasta, vendedor, cliente, zona, pagina ?? 1, tamanoPagina ?? 10, ct));
}).RequireAuthorization(policy => policy.RequireRole("A", "P", "Q", "V"));

app.MapGet("/api/pedidos/zonas", async (SqlServerRepository db, CancellationToken ct) => Results.Ok(await db.GetZonesAsync(ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "P", "S", "Q"));

app.MapGet("/api/vendedores", async (SqlServerRepository db, CancellationToken ct) => Results.Ok(await db.GetSellersAsync(ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "P", "S", "Q"));

app.MapGet("/api/clientes/buscar", async (string? texto, string? zona, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.SearchClientsAsync(texto, zona, ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "P", "S", "Q", "V"));

app.MapGet("/api/clientes/gestion/catalogos", async (SqlServerRepository db, CancellationToken ct) =>
{
    try { return Results.Ok(await db.GetClientCatalogsAsync(ct)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { mensaje = ex.Message }); }
}).RequireAuthorization(policy => policy.RequireRole("A", "V"));

app.MapGet("/api/clientes/gestion", async (string? texto, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.SearchManagedClientsAsync(texto, ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "V"));

app.MapPost("/api/clientes/gestion", async (ClienteGestionRequest request, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    try { return Results.Ok(await db.SaveManagedClientAsync(request, user.FindFirstValue("usuario") ?? "APP", ct)); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { mensaje = ex.Message }); }
}).RequireAuthorization(policy => policy.RequireRole("A", "V"));

app.MapGet("/api/clientes/{organizacion:int}/{codigo:int}/percepcion", async (int organizacion, int codigo, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetClientPerceptionAsync(organizacion, codigo, ct)))
    .RequireAuthorization(policy => policy.RequireRole("V"));

app.MapGet("/api/dashboard", async (int? desde, int? hasta, string? zona, string? cliente, string? vendedor, string? marca, string? rubro, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetDashboardAsync(desde, hasta, zona, cliente, vendedor, marca, rubro, ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "P", "Q"));

app.MapGet("/api/dashboard/facturacion", async (int? desde, int? hasta, int? opcion, string? zona, string? cliente, string? marca, string? rubro, int? articulo, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetFacturacionDashboardAsync(desde, hasta, opcion ?? 1, zona, cliente, marca, rubro, articulo, ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "S", "Q"));

app.MapGet("/api/dashboard/facturacion/filtros", async (SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetBillingFiltersAsync(ct))).RequireAuthorization(policy => policy.RequireRole("A", "P", "S", "Q"));

app.MapGet("/api/dashboard/anual", async (int? anio, int? opcion, string? zona, string? cliente, string? marca, string? rubro, int? articulo, int? limite, string? modo, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.GetAnnualDashboardAsync(anio ?? DateTime.Now.Year, opcion ?? 1, zona, cliente, marca, rubro, articulo, limite ?? 20, modo, ct)))
    .RequireAuthorization(policy => policy.RequireRole("A", "S", "Q"));

app.MapGet("/api/articulos/buscar", async (string? texto, string? marca, string? rubro, SqlServerRepository db, CancellationToken ct) =>
    Results.Ok(await db.SearchArticlesAsync(texto, marca, rubro, ct))).RequireAuthorization(policy => policy.RequireRole("A", "P", "S", "Q"));

app.MapGet("/api/pedidos/{organizacion:int}/{prefijo:int}/{remito:int}/{clienteCodigo:int}", async (int organizacion, int prefijo, int remito, int clienteCodigo, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    var vendedor = user.IsInRole("V") ? user.FindFirstValue("usuario") : null;
    return Results.Ok(await db.GetOrderDetailAsync(organizacion, prefijo, remito, clienteCodigo, vendedor, ct));
}).RequireAuthorization(policy => policy.RequireRole("A", "P", "Q", "V"));

app.MapPost("/api/pedidos/{organizacion:int}/{prefijo:int}/{remito:int}/{clienteCodigo:int}/editar", async (int organizacion, int prefijo, int remito, int clienteCodigo, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    var vendedor = user.FindFirstValue("usuario") ?? string.Empty;
    var result = await db.BeginOrderEditAsync(organizacion, prefijo, remito, clienteCodigo, vendedor, ct);
    return result.Status switch
    {
        "ok" => Results.Ok(result.Pedido),
        "billed" => Results.BadRequest(new { mensaje = "El pedido ya está facturado y no puede editarse." }),
        "locked" => Results.Conflict(new { mensaje = "El pedido no está disponible para edición." }),
        "forbidden" => Results.Forbid(),
        _ => Results.NotFound(new { mensaje = "El pedido no existe." })
    };
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapPut("/api/pedidos/{organizacion:int}/{prefijo:int}/{remito:int}/{clienteCodigo:int}/editar", async (int organizacion, int prefijo, int remito, int clienteCodigo, PedidoRequest pedido, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    var vendedor = user.FindFirstValue("usuario") ?? string.Empty;
    pedido = pedido with { Organizacion = organizacion, ClienteCodigo = clienteCodigo, Usuario = vendedor, Frio = prefijo == 18 };
    if (pedido.Items.Count == 0 || string.IsNullOrWhiteSpace(vendedor))
        return Results.BadRequest(new { mensaje = "El pedido debe tener al menos un artículo." });
    if (pedido.Items.Any(x => x.Cantidad <= 0 || x.Precio < 0 || x.ProductoCodigo <= 0 || string.IsNullOrWhiteSpace(x.Descripcion)))
        return Results.BadRequest(new { mensaje = "Hay renglones inválidos." });
    var result = await db.UpdateOrderAsync(organizacion, prefijo, remito, clienteCodigo, vendedor, pedido, ct);
    return result.Status switch
    {
        "ok" => Results.Ok(result.Pedido),
        "billed" => Results.BadRequest(new { mensaje = "El pedido ya está facturado y no puede editarse." }),
        "locked" => Results.Conflict(new { mensaje = "La edición del pedido ya no está activa." }),
        "forbidden" => Results.Forbid(),
        _ => Results.NotFound(new { mensaje = "El pedido no existe." })
    };
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapPost("/api/pedidos/{organizacion:int}/{prefijo:int}/{remito:int}/{clienteCodigo:int}/cancelar-edicion", async (int organizacion, int prefijo, int remito, int clienteCodigo, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    var vendedor = user.FindFirstValue("usuario") ?? string.Empty;
    var status = await db.CancelOrderEditAsync(organizacion, prefijo, remito, clienteCodigo, vendedor, ct);
    return status switch
    {
        "ok" => Results.Ok(new { mensaje = "Edición cancelada." }),
        "billed" => Results.BadRequest(new { mensaje = "El pedido ya está facturado." }),
        "forbidden" => Results.Forbid(),
        _ => Results.NotFound(new { mensaje = "El pedido no existe." })
    };
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapDelete("/api/pedidos/{organizacion:int}/{prefijo:int}/{remito:int}/{clienteCodigo:int}", async (int organizacion, int prefijo, int remito, int clienteCodigo, ClaimsPrincipal user, SqlServerRepository db, CancellationToken ct) =>
{
    var vendedor = user.FindFirstValue("usuario") ?? string.Empty;
    var result = await db.DeleteOwnUnbilledOrderAsync(organizacion, prefijo, remito, clienteCodigo, vendedor, ct);
    return result switch
    {
        "ok" => Results.Ok(new { mensaje = "Pedido eliminado." }),
        "billed" => Results.BadRequest(new { mensaje = "El pedido ya está facturado y no puede eliminarse." }),
        "locked" => Results.Conflict(new { mensaje = "El pedido está siendo editado y no puede eliminarse." }),
        "forbidden" => Results.Forbid(),
        _ => Results.NotFound(new { mensaje = "El pedido no existe." })
    };
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.MapGet("/api/pedidos/{organizacion:int}/{prefijo:int}/{remito:int}/{clienteCodigo:int}/pdf", async (int organizacion, int prefijo, int remito, int clienteCodigo, ClaimsPrincipal user, SqlServerRepository db, TenantContext tenant, IWebHostEnvironment environment, CancellationToken ct) =>
{
    var vendedor = user.IsInRole("V") ? user.FindFirstValue("usuario") : null;
    var pedido = await db.GetOrderPdfDataAsync(organizacion, prefijo, remito, clienteCodigo, vendedor, ct);
    byte[]? logo = null;
    if (pedido is not null && tenant.Logo.StartsWith('/') && !tenant.Logo.Equals("/icons/icon.svg", StringComparison.OrdinalIgnoreCase))
    {
        var webRoot = Path.GetFullPath(environment.WebRootPath) + Path.DirectorySeparatorChar;
        var logoPath = Path.GetFullPath(Path.Combine(environment.WebRootPath, tenant.Logo.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (logoPath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(logoPath)) logo = await File.ReadAllBytesAsync(logoPath, ct);
    }
    return pedido is null
        ? Results.NotFound()
        : Results.File(PedidoPdfGenerator.Generate(pedido, tenant.Subtotales, logo), "application/pdf", $"Pedido_{prefijo:0000}-{remito:00000000}.pdf");
}).RequireAuthorization(policy => policy.RequireRole("A", "P", "Q", "V"));

app.MapPost("/api/pedidos", async (PedidoRequest pedido, ClaimsPrincipal user, SqlServerRepository db, TenantContext tenant, CancellationToken ct) =>
{
    var frioHabilitado = !tenant.Funciones.TryGetValue("Frio", out var permiteFrio) || permiteFrio;
    var offlineHabilitado = tenant.Funciones.TryGetValue("PedidosOffline", out var permiteOffline) && permiteOffline;
    pedido = pedido with
    {
        Usuario = user.FindFirstValue("usuario") ?? user.Identity?.Name ?? "APP",
        Frio = frioHabilitado && pedido.Frio,
        IdOrigenApp = offlineHabilitado ? pedido.IdOrigenApp?.Trim() : null
    };
    if (pedido.Organizacion <= 0 || pedido.ClienteCodigo <= 0 || pedido.Items.Count == 0 || string.IsNullOrWhiteSpace(pedido.Usuario))
        return Results.BadRequest(new { error = "Pedido incompleto" });
    if (pedido.Items.Any(x => x.Cantidad <= 0 || x.Precio < 0 ||
                              x.ProductoCodigo <= 0 || string.IsNullOrWhiteSpace(x.Descripcion)))
        return Results.BadRequest(new { error = "Hay renglones inválidos" });
    if (pedido.IdOrigenApp?.Length > 50)
        return Results.BadRequest(new { error = "Identificador de origen inválido" });

    var result = await db.SaveOrderAsync(pedido, ct);
    return Results.Ok(result);
}).RequireAuthorization(policy => policy.RequireRole("V"));

app.Run();

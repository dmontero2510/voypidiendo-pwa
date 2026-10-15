using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using VoyPidiendo.SuperAdmin;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CentralRepository>();
builder.Services.AddDataProtection().SetApplicationName("VoyPidiendo.SuperAdmin");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "VoyPidiendo.SuperAdmin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/setup/status", async (CentralRepository db, CancellationToken ct) => Results.Ok(new { requiereConfiguracion = await db.UserCountAsync(ct) == 0 })).AllowAnonymous();
app.MapPost("/api/setup", async (BootstrapRequest request, IConfiguration config, CentralRepository db, CancellationToken ct) =>
{
    if (request.ClaveInicial != config["Security:BootstrapKey"]) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Usuario) || string.IsNullOrWhiteSpace(request.Nombre) || request.Clave.Length < 8) return Results.BadRequest(new { mensaje = "Complete los datos y use una clave de al menos 8 caracteres." });
    return await db.BootstrapAsync(request, ct) ? Results.Ok() : Results.BadRequest(new { mensaje = "El Superadministrador inicial ya fue creado." });
}).AllowAnonymous();
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, CentralRepository db, CancellationToken ct) =>
{
    var principal = await db.LoginAsync(request, ct);
    if (principal is null) return Results.Unauthorized();
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Ok(new { nombre = principal.Identity?.Name });
}).AllowAnonymous();
app.MapPost("/api/auth/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
app.MapGet("/api/auth/me", (HttpContext context) => Results.Ok(new { nombre = context.User.Identity?.Name })).RequireAuthorization();
app.MapGet("/api/empresas", async (CentralRepository db, CancellationToken ct) => Results.Ok(await db.ListCompaniesAsync(ct))).RequireAuthorization();
app.MapPost("/api/empresas", async (EmpresaRequest request, CentralRepository db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Codigo) || string.IsNullOrWhiteSpace(request.Nombre) || string.IsNullOrWhiteSpace(request.Titulo)) return Results.BadRequest(new { mensaje = "Complete código, nombre y título." });
    return Results.Ok(new { id = await db.SaveCompanyAsync(request, ct) });
}).RequireAuthorization();
app.MapPost("/api/empresas/{id:int}/probar-conexion", async (int id, CentralRepository db, CancellationToken ct) => { await db.TestConnectionAsync(id, ct); return Results.Ok(new { mensaje = "Conexión correcta." }); }).RequireAuthorization();

app.Run();

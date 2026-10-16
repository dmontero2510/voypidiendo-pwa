using System.Data;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace VoyPidiendo.Api;

public sealed class TenantContext
{
    public string Id { get; internal set; } = string.Empty;
    public string Nombre { get; internal set; } = string.Empty;
    public string Titulo { get; internal set; } = "Voy Pidiendo";
    public string Subtitulo { get; internal set; } = "Toma de pedidos";
    public string Logo { get; internal set; } = "/icons/icon.svg";
    public string ColorPrincipal { get; internal set; } = "#20b3df";
    public string ColorSecundario { get; internal set; } = "#10213c";
    public string ConnectionString { get; internal set; } = string.Empty;
    public IReadOnlyDictionary<string, bool> Funciones { get; internal set; } = new Dictionary<string, bool>();
    public bool PercepIibb { get; internal set; } = true;
    public bool Subtotales { get; internal set; } = true;
}

public sealed class TenantCatalog(IConfiguration configuration, IDataProtectionProvider protectionProvider, IMemoryCache cache)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("VoyPidiendo.Central.SqlConnections.v1");

    public async Task<TenantContext> ResolveAsync(string host, string? requestedCode, CancellationToken ct)
    {
        var central = configuration.GetConnectionString("Central")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Central.");
        host = host.Split(':')[0].Trim().ToLowerInvariant();
        requestedCode = requestedCode?.Trim().ToLowerInvariant();
        var cacheKey = $"tenant:{requestedCode ?? ""}:{host}";
        if (cache.TryGetValue<TenantContext>(cacheKey, out var cached) && cached is not null) return cached;
        await using var cn = new SqlConnection(central);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT TOP (1) e.ID_EMPRESA,e.CODIGO,e.NOMBRE,e.TITULO,ISNULL(e.SUBTITULO,''),ISNULL(e.LOGO_URL,''),
                   e.COLOR_PRINCIPAL,e.COLOR_SECUNDARIO,e.CONEXION_CIFRADA,e.PERCEP_IIBB,e.SUBTOTALES
            FROM dbo.SA_EMPRESAS e
            WHERE e.HABILITADA=1
              AND (e.VENCIMIENTO IS NULL OR e.VENCIMIENTO>=CONVERT(date,GETDATE()))
              AND
              (
                  (@codigo<>'' AND e.CODIGO=@codigo)
                  OR (@codigo='' AND EXISTS
                      (SELECT 1 FROM dbo.SA_DOMINIOS d WHERE d.ID_EMPRESA=e.ID_EMPRESA AND d.HABILITADO=1 AND d.DOMINIO=@host))
                  OR (@codigo='' AND @local=1)
              )
            ORDER BY CASE WHEN e.CODIGO=@codigo THEN 0 ELSE 1 END,e.ID_EMPRESA
            """, cn);
        cmd.Parameters.Add("@codigo", SqlDbType.VarChar, 40).Value = requestedCode ?? string.Empty;
        cmd.Parameters.Add("@host", SqlDbType.VarChar, 200).Value = host;
        cmd.Parameters.Add("@local", SqlDbType.Bit).Value = host is "localhost" or "127.0.0.1";
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw new InvalidOperationException("No existe una empresa habilitada para este acceso.");
        if (rd.IsDBNull(8)) throw new InvalidOperationException("La empresa no tiene conexión SQL configurada.");
        var companyId = rd.GetInt32(0);
        var tenant = new TenantContext
        {
            Id = rd.GetString(1),
            Nombre = rd.GetString(2),
            Titulo = rd.GetString(3),
            Subtitulo = rd.GetString(4),
            Logo = string.IsNullOrWhiteSpace(rd.GetString(5)) ? "/icons/icon.svg" : rd.GetString(5),
            ColorPrincipal = rd.GetString(6),
            ColorSecundario = rd.GetString(7),
            ConnectionString = _protector.Unprotect(Encoding.UTF8.GetString((byte[])rd[8])),
            PercepIibb = rd.GetBoolean(9),
            Subtotales = rd.GetBoolean(10)
        };
        await rd.CloseAsync();
        await using var functions = new SqlCommand("SELECT FUNCION,HABILITADA FROM dbo.SA_FUNCIONES WHERE ID_EMPRESA=@id", cn);
        functions.Parameters.Add("@id", SqlDbType.Int).Value = companyId;
        await using var fr = await functions.ExecuteReaderAsync(ct);
        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        while (await fr.ReadAsync(ct)) values[fr.GetString(0)] = fr.GetBoolean(1);
        tenant.Funciones = values;
        cache.Set(cacheKey, tenant, TimeSpan.FromSeconds(30));
        return tenant;
    }

    public static void Copy(TenantContext source, TenantContext destination)
    {
        destination.Id = source.Id;
        destination.Nombre = source.Nombre;
        destination.Titulo = source.Titulo;
        destination.Subtitulo = source.Subtitulo;
        destination.Logo = source.Logo;
        destination.ColorPrincipal = source.ColorPrincipal;
        destination.ColorSecundario = source.ColorSecundario;
        destination.ConnectionString = source.ConnectionString;
        destination.Funciones = source.Funciones;
        destination.PercepIibb = source.PercepIibb;
        destination.Subtotales = source.Subtotales;
    }
}

public sealed record TenantPublicConfig(string Codigo, string Nombre, string Titulo, string Subtitulo, string Logo, string ColorPrincipal, string ColorSecundario, IReadOnlyDictionary<string, bool> Funciones, bool PercepIibb, bool Subtotales);

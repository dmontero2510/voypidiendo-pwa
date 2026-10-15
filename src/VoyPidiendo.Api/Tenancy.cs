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

public sealed class TenantCatalog(IConfiguration configuration)
{
    public TenantContext Resolve(string host)
    {
        host = host.Split(':')[0].Trim().ToLowerInvariant();
        var tenants = configuration.GetSection("Tenants:Items").GetChildren().ToList();
        var selected = tenants.FirstOrDefault(x => x.GetSection("Hosts").Get<string[]>()?.Any(h =>
            string.Equals(h.Trim(), host, StringComparison.OrdinalIgnoreCase)) == true);

        if (selected is null)
        {
            var defaultId = configuration["Tenants:DefaultTenant"];
            selected = tenants.FirstOrDefault(x => string.Equals(x.Key, defaultId, StringComparison.OrdinalIgnoreCase));
        }

        if (selected is null) throw new InvalidOperationException($"No existe una empresa configurada para el dominio '{host}'.");
        var connectionName = selected["ConnectionStringName"] ?? $"Tenant_{selected.Key}";
        var connectionString = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Falta ConnectionStrings:{connectionName} para la empresa {selected.Key}.");

        return new TenantContext
        {
            Id = selected.Key,
            Nombre = selected["Nombre"] ?? selected.Key,
            Titulo = selected["Titulo"] ?? "Voy Pidiendo",
            Subtitulo = selected["Subtitulo"] ?? "Toma de pedidos",
            Logo = selected["Logo"] ?? "/icons/icon.svg",
            ColorPrincipal = selected["ColorPrincipal"] ?? "#20b3df",
            ColorSecundario = selected["ColorSecundario"] ?? "#10213c",
            ConnectionString = connectionString,
            Funciones = selected.GetSection("Funciones").GetChildren().ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Get<bool>(), StringComparer.OrdinalIgnoreCase),
            PercepIibb = selected.GetValue("Pedidos:PercepIIBB", true),
            Subtotales = selected.GetValue("Pedidos:Subtotales", true)
        };
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

public sealed record TenantPublicConfig(
    string Codigo,
    string Nombre,
    string Titulo,
    string Subtitulo,
    string Logo,
    string ColorPrincipal,
    string ColorSecundario,
    IReadOnlyDictionary<string, bool> Funciones,
    bool PercepIibb,
    bool Subtotales);

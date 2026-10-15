using System.Data;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace VoyPidiendo.SuperAdmin;

public sealed class CentralRepository(
    IConfiguration configuration,
    IDataProtectionProvider protectionProvider,
    IHttpContextAccessor accessor)
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("VoyPidiendo.Central.SqlConnections.v1");
    private SqlConnection CreateConnection() => new(configuration.GetConnectionString("Central")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:Central"));

    public async Task<int> UserCountAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.SA_USUARIOS", cn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> BootstrapAsync(BootstrapRequest request, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.SA_USUARIOS WITH(UPDLOCK,HOLDLOCK)", cn, tx);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0) return false;
        await using var cmd = new SqlCommand("INSERT dbo.SA_USUARIOS(USUARIO,NOMBRE,CLAVE_HASH) VALUES(@u,@n,@h)", cn, tx);
        cmd.Parameters.Add("@u", SqlDbType.VarChar, 50).Value = request.Usuario.Trim();
        cmd.Parameters.Add("@n", SqlDbType.VarChar, 100).Value = request.Nombre.Trim();
        cmd.Parameters.Add("@h", SqlDbType.VarChar, 100).Value = BCrypt.Net.BCrypt.HashPassword(request.Clave);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<ClaimsPrincipal?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT ID_USUARIO,NOMBRE,CLAVE_HASH,HABILITADO FROM dbo.SA_USUARIOS WHERE USUARIO=@u", cn);
        cmd.Parameters.Add("@u", SqlDbType.VarChar, 50).Value = request.Usuario.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || !rd.GetBoolean(3) || !BCrypt.Net.BCrypt.Verify(request.Clave, rd.GetString(2))) return null;
        var id = rd.GetInt32(0);
        var nombre = rd.GetString(1);
        await rd.CloseAsync();
        await using var update = new SqlCommand("UPDATE dbo.SA_USUARIOS SET ULTIMO_ACCESO=SYSDATETIME() WHERE ID_USUARIO=@id", cn);
        update.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await update.ExecuteNonQueryAsync(ct);
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Name, nombre),
            new Claim(ClaimTypes.Role, "SA")], "SuperAdmin"));
    }

    public async Task<IReadOnlyList<EmpresaDto>> ListCompaniesAsync(CancellationToken ct)
    {
        var functions = await LoadFunctionsAsync(ct);
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT e.ID_EMPRESA,e.CODIGO,e.NOMBRE,e.TITULO,ISNULL(e.SUBTITULO,''),ISNULL(e.LOGO_URL,''),e.COLOR_PRINCIPAL,e.COLOR_SECUNDARIO,
              ISNULL((SELECT TOP(1)d.DOMINIO FROM dbo.SA_DOMINIOS d WHERE d.ID_EMPRESA=e.ID_EMPRESA AND d.HABILITADO=1 ORDER BY d.PRINCIPAL DESC,d.ID_DOMINIO),''),
              ISNULL(e.CONEXION_REFERENCIA,''),CASE WHEN e.CONEXION_CIFRADA IS NULL THEN 0 ELSE 1 END,e.PERCEP_IIBB,e.SUBTOTALES,e.HABILITADA,e.VENCIMIENTO,e.ULTIMA_ACTIVIDAD
            FROM dbo.SA_EMPRESAS e ORDER BY e.NOMBRE
            """, cn);
        var result = new List<EmpresaDto>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var id = rd.GetInt32(0);
            result.Add(new(id, rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetString(8), rd.GetString(9), rd.GetBoolean(10), rd.GetBoolean(11), rd.GetBoolean(12), rd.GetBoolean(13), rd.IsDBNull(14) ? null : rd.GetDateTime(14), rd.IsDBNull(15) ? null : rd.GetDateTime(15), functions.GetValueOrDefault(id) ?? new Dictionary<string, bool>()));
        }
        return result;
    }

    private async Task<Dictionary<int, IReadOnlyDictionary<string, bool>>> LoadFunctionsAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT ID_EMPRESA,FUNCION,HABILITADA FROM dbo.SA_FUNCIONES", cn);
        var temp = new Dictionary<int, Dictionary<string, bool>>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var id = rd.GetInt32(0);
            if (!temp.TryGetValue(id, out var values)) temp[id] = values = new(StringComparer.OrdinalIgnoreCase);
            values[rd.GetString(1)] = rd.GetBoolean(2);
        }
        return temp.ToDictionary(x => x.Key, x => (IReadOnlyDictionary<string, bool>)x.Value);
    }

    public async Task<int> SaveCompanyAsync(EmpresaRequest request, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        var encrypted = string.IsNullOrWhiteSpace(request.Conexion) ? null : Encoding.UTF8.GetBytes(_protector.Protect(request.Conexion.Trim()));
        var sql = request.Id is null ? """
            INSERT dbo.SA_EMPRESAS(CODIGO,NOMBRE,TITULO,SUBTITULO,LOGO_URL,COLOR_PRINCIPAL,COLOR_SECUNDARIO,CONEXION_CIFRADA,CONEXION_REFERENCIA,PERCEP_IIBB,SUBTOTALES,HABILITADA,VENCIMIENTO)
            OUTPUT INSERTED.ID_EMPRESA VALUES(@codigo,@nombre,@titulo,@subtitulo,@logo,@color1,@color2,@conexion,@referencia,@percep,@subtotales,@habilitada,@vencimiento)
            """ : """
            UPDATE dbo.SA_EMPRESAS SET CODIGO=@codigo,NOMBRE=@nombre,TITULO=@titulo,SUBTITULO=@subtitulo,LOGO_URL=@logo,COLOR_PRINCIPAL=@color1,COLOR_SECUNDARIO=@color2,
              CONEXION_CIFRADA=COALESCE(@conexion,CONEXION_CIFRADA),CONEXION_REFERENCIA=@referencia,PERCEP_IIBB=@percep,SUBTOTALES=@subtotales,HABILITADA=@habilitada,VENCIMIENTO=@vencimiento
            OUTPUT INSERTED.ID_EMPRESA WHERE ID_EMPRESA=@id
            """;
        await using var cmd = new SqlCommand(sql, cn, tx);
        AddCompanyParameters(cmd, request, encrypted);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await ReplaceDomainAsync(cn, tx, id, request.Dominio, ct);
        await ReplaceFunctionsAsync(cn, tx, id, request.Funciones ?? [], ct);
        await AuditAsync(cn, tx, id, request.Id is null ? "EMPRESA_CREADA" : "EMPRESA_MODIFICADA", request.Nombre, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    private static void AddCompanyParameters(SqlCommand cmd, EmpresaRequest r, byte[]? encrypted)
    {
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = (object?)r.Id ?? DBNull.Value;
        cmd.Parameters.Add("@codigo", SqlDbType.VarChar, 40).Value = r.Codigo.Trim().ToLowerInvariant();
        cmd.Parameters.Add("@nombre", SqlDbType.VarChar, 120).Value = r.Nombre.Trim();
        cmd.Parameters.Add("@titulo", SqlDbType.VarChar, 80).Value = r.Titulo.Trim();
        cmd.Parameters.Add("@subtitulo", SqlDbType.VarChar, 150).Value = (object?)r.Subtitulo?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@logo", SqlDbType.VarChar, 300).Value = (object?)r.LogoUrl?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@color1", SqlDbType.VarChar, 20).Value = r.ColorPrincipal;
        cmd.Parameters.Add("@color2", SqlDbType.VarChar, 20).Value = r.ColorSecundario;
        cmd.Parameters.Add("@conexion", SqlDbType.VarBinary, -1).Value = (object?)encrypted ?? DBNull.Value;
        cmd.Parameters.Add("@referencia", SqlDbType.VarChar, 150).Value = (object?)r.ConexionReferencia?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@percep", SqlDbType.Bit).Value = r.PercepIibb;
        cmd.Parameters.Add("@subtotales", SqlDbType.Bit).Value = r.Subtotales;
        cmd.Parameters.Add("@habilitada", SqlDbType.Bit).Value = r.Habilitada;
        cmd.Parameters.Add("@vencimiento", SqlDbType.Date).Value = (object?)r.Vencimiento?.Date ?? DBNull.Value;
    }

    private static async Task ReplaceDomainAsync(SqlConnection cn, SqlTransaction tx, int id, string? domain, CancellationToken ct)
    {
        await using (var del = new SqlCommand("DELETE dbo.SA_DOMINIOS WHERE ID_EMPRESA=@id", cn, tx)) { del.Parameters.Add("@id", SqlDbType.Int).Value = id; await del.ExecuteNonQueryAsync(ct); }
        if (string.IsNullOrWhiteSpace(domain)) return;
        await using var add = new SqlCommand("INSERT dbo.SA_DOMINIOS(ID_EMPRESA,DOMINIO,PRINCIPAL) VALUES(@id,@domain,1)", cn, tx);
        add.Parameters.Add("@id", SqlDbType.Int).Value = id;
        add.Parameters.Add("@domain", SqlDbType.VarChar, 200).Value = domain.Trim().ToLowerInvariant();
        await add.ExecuteNonQueryAsync(ct);
    }

    private static async Task ReplaceFunctionsAsync(SqlConnection cn, SqlTransaction tx, int id, IEnumerable<KeyValuePair<string, bool>> functions, CancellationToken ct)
    {
        await using (var del = new SqlCommand("DELETE dbo.SA_FUNCIONES WHERE ID_EMPRESA=@id", cn, tx)) { del.Parameters.Add("@id", SqlDbType.Int).Value = id; await del.ExecuteNonQueryAsync(ct); }
        foreach (var item in functions)
        {
            await using var add = new SqlCommand("INSERT dbo.SA_FUNCIONES(ID_EMPRESA,FUNCION,HABILITADA) VALUES(@id,@function,@enabled)", cn, tx);
            add.Parameters.Add("@id", SqlDbType.Int).Value = id;
            add.Parameters.Add("@function", SqlDbType.VarChar, 60).Value = item.Key;
            add.Parameters.Add("@enabled", SqlDbType.Bit).Value = item.Value;
            await add.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task TestConnectionAsync(int id, CancellationToken ct)
    {
        await using var cn = CreateConnection(); await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT CONEXION_CIFRADA FROM dbo.SA_EMPRESAS WHERE ID_EMPRESA=@id", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw is not byte[] bytes) throw new InvalidOperationException("La empresa no tiene conexión configurada.");
        var text = _protector.Unprotect(Encoding.UTF8.GetString(bytes));
        await using var target = new SqlConnection(text); await target.OpenAsync(ct);
        await using var test = new SqlCommand("SELECT 1", target); await test.ExecuteScalarAsync(ct);
    }

    private async Task AuditAsync(SqlConnection cn, SqlTransaction tx, int? companyId, string action, string detail, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("INSERT dbo.SA_AUDITORIA(ID_USUARIO,ID_EMPRESA,ACCION,DETALLE,IP) VALUES(@user,@company,@action,@detail,@ip)", cn, tx);
        cmd.Parameters.Add("@user", SqlDbType.Int).Value = int.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : DBNull.Value;
        cmd.Parameters.Add("@company", SqlDbType.Int).Value = (object?)companyId ?? DBNull.Value;
        cmd.Parameters.Add("@action", SqlDbType.VarChar, 80).Value = action;
        cmd.Parameters.Add("@detail", SqlDbType.VarChar, 1000).Value = detail;
        cmd.Parameters.Add("@ip", SqlDbType.VarChar, 60).Value = (object?)accessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;

namespace VoyPidiendo.Api;

public sealed record LoginRequest(string Usuario, string Clave);
public sealed record BootstrapRequest(string Usuario, string Nombre, string Clave);
public sealed record UsuarioPerfil(int Id, string Usuario, string Nombre, string Rol, int? CodVend);
public sealed record LoginResponse(string Token, UsuarioPerfil Usuario);
public sealed record CrearUsuarioRequest(string Usuario, string Nombre, string Clave, string Rol);
public sealed record RestablecerClaveRequest(string Clave);
public sealed record UsuarioListado(int Id, string Usuario, string Nombre, string Rol, bool Habilitado, int? CodVend);
public sealed record TituloAplicacion(string Titulo, string Subtitulo);
internal sealed record UsuarioAuth(int Id, string Usuario, string Nombre, string ClaveHash, string Rol, bool Habilitado, int? CodVend);

public sealed class AuthService(IConfiguration configuration, TenantContext tenant)
{
    private SqlConnection CreateConnection() => new(tenant.ConnectionString);

    private async Task<UsuarioAuth?> FindAsync(string usuario, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT ID_USUARIO, USUARIO, NOMBRE, CLAVE_HASH, ROL, HABILITADO, COD_VEND
            FROM dbo.APP_USUARIOS
            WHERE USUARIO = @usuario
            """, cn);
        cmd.Parameters.Add("@usuario", SqlDbType.VarChar, 50).Value = usuario.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4).Trim(), rd.GetBoolean(5), rd.IsDBNull(6) ? null : rd.GetInt32(6));
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await FindAsync(request.Usuario, ct);
        if (user is null || !user.Habilitado || !BCrypt.Net.BCrypt.Verify(request.Clave, user.ClaveHash)) return null;
        var profile = new UsuarioPerfil(user.Id, user.Usuario, user.Nombre, user.Rol, user.CodVend);
        return new(CreateToken(profile), profile);
    }

    public async Task<(bool Created, string Message)> BootstrapAsync(BootstrapRequest request, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var count = new SqlCommand("SELECT COUNT(1) FROM dbo.APP_USUARIOS WITH (UPDLOCK, HOLDLOCK)", cn, tx);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0)
        {
            await tx.RollbackAsync(ct);
            return (false, "Ya existe al menos un usuario.");
        }
        await using var insert = new SqlCommand("""
            INSERT INTO dbo.APP_USUARIOS (USUARIO, NOMBRE, CLAVE_HASH, ROL, HABILITADO)
            VALUES (@usuario, @nombre, @hash, 'A', 1)
            """, cn, tx);
        insert.Parameters.Add("@usuario", SqlDbType.VarChar, 50).Value = request.Usuario.Trim();
        insert.Parameters.Add("@nombre", SqlDbType.VarChar, 100).Value = request.Nombre.Trim();
        insert.Parameters.Add("@hash", SqlDbType.VarChar, 100).Value = BCrypt.Net.BCrypt.HashPassword(request.Clave);
        await insert.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return (true, "Administrador inicial creado.");
    }

    public async Task<IReadOnlyList<UsuarioListado>> ListAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT ID_USUARIO, USUARIO, NOMBRE, ROL, HABILITADO, COD_VEND
            FROM dbo.APP_USUARIOS
            ORDER BY NOMBRE
            """, cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<UsuarioListado>();
        while (await rd.ReadAsync(ct))
            result.Add(new(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3).Trim(), rd.GetBoolean(4), rd.IsDBNull(5) ? null : rd.GetInt32(5)));
        return result;
    }

    public async Task<(bool Created, string Message)> CreateUserAsync(CrearUsuarioRequest request, CancellationToken ct)
    {
        var role = request.Rol.Trim().ToUpperInvariant();
        if (role is not ("A" or "V" or "P" or "S" or "Q")) return (false, "El rol debe ser A, V, P, S o Q.");
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            IF EXISTS
            (
                SELECT 1
                FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID('dbo.APP_USUARIOS')
                  AND name = 'CK_APP_USUARIOS_ROL'
                  AND definition NOT LIKE '%''P''%'
            )
            BEGIN
                ALTER TABLE dbo.APP_USUARIOS DROP CONSTRAINT CK_APP_USUARIOS_ROL
                ALTER TABLE dbo.APP_USUARIOS ADD CONSTRAINT CK_APP_USUARIOS_ROL CHECK (ROL IN ('A','V','P','S','Q'))
            END

            IF EXISTS (SELECT 1 FROM dbo.APP_USUARIOS WHERE USUARIO=@usuario)
                SELECT CAST(0 AS bit)
            ELSE
            BEGIN
                INSERT INTO dbo.APP_USUARIOS (USUARIO, NOMBRE, CLAVE_HASH, ROL, HABILITADO, COD_VEND)
                VALUES (@usuario, @nombre, @hash, @rol, 1, CASE WHEN @rol='V' THEN 2 ELSE NULL END)
                SELECT CAST(1 AS bit)
            END
            """, cn);
        cmd.Parameters.Add("@usuario", SqlDbType.VarChar, 50).Value = request.Usuario.Trim();
        cmd.Parameters.Add("@nombre", SqlDbType.VarChar, 100).Value = request.Nombre.Trim();
        cmd.Parameters.Add("@hash", SqlDbType.VarChar, 100).Value = BCrypt.Net.BCrypt.HashPassword(request.Clave);
        cmd.Parameters.Add("@rol", SqlDbType.Char, 1).Value = role;
        var created = Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct));
        return created ? (true, "Usuario creado.") : (false, "Ya existe ese usuario.");
    }

    public async Task<TituloAplicacion> GetTitleAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT TOP (1) ISNULL(TITULO,''),ISNULL(SUBTITULO,'') FROM dbo.APP_TITULO", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return new("Voy Pidiendo", "TOMA DE PEDIDOS");
        var titulo = rd.GetString(0).Trim();
        var subtitulo = rd.GetString(1).Trim();
        return new(string.IsNullOrWhiteSpace(titulo) ? "Voy Pidiendo" : titulo, string.IsNullOrWhiteSpace(subtitulo) ? "TOMA DE PEDIDOS" : subtitulo);
    }

    public async Task SaveTitleAsync(TituloAplicacion value, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.APP_TITULO SET TITULO=@titulo,SUBTITULO=@subtitulo; IF @@ROWCOUNT=0 INSERT INTO dbo.APP_TITULO(TITULO,SUBTITULO) VALUES(@titulo,@subtitulo)", cn);
        cmd.Parameters.Add("@titulo", SqlDbType.VarChar, 50).Value = value.Titulo.Trim();
        cmd.Parameters.Add("@subtitulo", SqlDbType.VarChar, 150).Value = value.Subtitulo.Trim();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SetEnabledAsync(int id, bool enabled, int currentUserId, CancellationToken ct)
    {
        if (id == currentUserId && !enabled) return false;
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.APP_USUARIOS SET HABILITADO=@habilitado WHERE ID_USUARIO=@id", cn);
        cmd.Parameters.Add("@habilitado", SqlDbType.Bit).Value = enabled;
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ResetPasswordAsync(int id, string password, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.APP_USUARIOS SET CLAVE_HASH=@hash WHERE ID_USUARIO=@id", cn);
        cmd.Parameters.Add("@hash", SqlDbType.VarChar, 100).Value = BCrypt.Net.BCrypt.HashPassword(password);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private string CreateToken(UsuarioPerfil user)
    {
        var key = configuration["Jwt:Key"] ?? configuration["Security:ApiKey"]
            ?? throw new InvalidOperationException("Falta Jwt:Key.");
        if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException("Jwt:Key debe tener al menos 32 caracteres.");
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Nombre),
            new Claim(ClaimTypes.Role, user.Rol),
            new Claim("usuario", user.Usuario),
            new Claim("cod_vend", user.CodVend?.ToString() ?? string.Empty),
            new Claim("tenant", tenant.Id)
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("VoyPidiendo", "VoyPidiendo", claims, expires: DateTime.UtcNow.AddHours(12), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

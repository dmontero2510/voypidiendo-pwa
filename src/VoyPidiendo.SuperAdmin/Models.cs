namespace VoyPidiendo.SuperAdmin;

public sealed record LoginRequest(string Usuario, string Clave);
public sealed record BootstrapRequest(string Usuario, string Nombre, string Clave, string ClaveInicial);
public sealed record EmpresaDto(int Id, string Codigo, string Nombre, string Titulo, string Subtitulo, string LogoUrl, string ColorPrincipal, string ColorSecundario, string Dominio, string ConexionReferencia, bool TieneConexion, bool PercepIibb, bool Subtotales, bool Habilitada, DateTime? Vencimiento, DateTime? UltimaActividad, IReadOnlyDictionary<string, bool> Funciones);
public sealed record EmpresaRequest(int? Id, string Codigo, string Nombre, string Titulo, string? Subtitulo, string? LogoUrl, string ColorPrincipal, string ColorSecundario, string? Dominio, string? Conexion, string? ConexionReferencia, bool PercepIibb, bool Subtotales, bool Habilitada, DateTime? Vencimiento, Dictionary<string, bool>? Funciones);
public sealed record EmpresaUsuarioDto(int Id, string Usuario, string Nombre, string Rol, bool Habilitado, int? CodVend);
public sealed record EmpresaUsuarioRequest(string Usuario, string Nombre, string Clave, string Rol, int? CodVend);
public sealed record CambioClaveRequest(string Clave);
public sealed record ValidacionObjetoDto(string Objeto, string Tipo, bool Correcto, string Detalle);
public sealed record ValidacionEmpresaDto(bool Correcta, int Correctos, int Total, IReadOnlyList<ValidacionObjetoDto> Objetos);

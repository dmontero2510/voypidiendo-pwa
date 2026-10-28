using System.Data;
using Microsoft.Data.SqlClient;

namespace VoyPidiendo.Api;

public sealed class SqlServerRepository(TenantContext tenant)
{
    private sealed record DashboardRow(int Org, int Pref, int Remito, int Cliente, int Fecha, decimal TotalPedido, string Usuario, string ClienteNombre, string Zona, int? IdLinea, decimal TotalLinea, string Rubro, string Grupo, int? Articulo, string Descripcion, decimal Cantidad);
    private sealed record FacturacionHeaderRow(int Fecha, string Tipo, int Cliente, string Nombre, decimal Total);
    private sealed record FacturacionDetailRow(string Tipo, int? Articulo, string Descripcion, string Marca, string Rubro, decimal Total, decimal Cantidad);
    private sealed record AnualDetailRow(int Fecha, string Tipo, int Cliente, string ClienteNombre, int Articulo, string Descripcion, string Marca, string Rubro, decimal Total, decimal Cantidad);
    private const string VisitSelect = """
        SELECT v.ID_VISITA,v.USUARIO,v.ORGANIZACION,v.COD_CLIENTE,ISNULL(c.NOMBRE,''),ISNULL(c.ZONA,''),
               v.FECHA_LLEGADA,v.FECHA_SALIDA,DATEDIFF(minute,v.FECHA_LLEGADA,ISNULL(v.FECHA_SALIDA,GETDATE())),
               ISNULL(v.LAT_SALIDA,v.LAT_LLEGADA),ISNULL(v.LON_SALIDA,v.LON_LLEGADA)
        FROM dbo.APP_VISITAS v
        OUTER APPLY (SELECT TOP (1) NOMBRE,ZONA FROM dbo.VW_APP_CLIENTES vc WHERE vc.ORGANIZACION=v.ORGANIZACION AND vc.CODIGO=v.COD_CLIENTE) c
        """;

    private SqlConnection CreateConnection() => new(tenant.ConnectionString);

    public async Task<VisitaDto?> GetActiveVisitAsync(int userId, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(VisitSelect + " WHERE v.ID_USUARIO=@usuario AND v.FECHA_SALIDA IS NULL ORDER BY v.FECHA_LLEGADA DESC", cn);
        cmd.Parameters.Add("@usuario", SqlDbType.Int).Value = userId;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadVisit(rd) : null;
    }

    public async Task<VisitaDto?> StartVisitAsync(int userId, string username, VisitaRequest request, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using (var check = new SqlCommand("SELECT COUNT(*) FROM dbo.APP_VISITAS WITH (UPDLOCK, HOLDLOCK) WHERE ID_USUARIO=@usuario AND FECHA_SALIDA IS NULL", cn, (SqlTransaction)tx))
        {
            check.Parameters.Add("@usuario", SqlDbType.Int).Value = userId;
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) > 0) return null;
        }
        int id;
        await using (var insert = new SqlCommand("INSERT dbo.APP_VISITAS(ID_USUARIO,USUARIO,ORGANIZACION,COD_CLIENTE,LAT_LLEGADA,LON_LLEGADA) VALUES(@id,@usuario,@org,@cliente,@lat,@lon); SELECT CAST(SCOPE_IDENTITY() AS int);", cn, (SqlTransaction)tx))
        {
            insert.Parameters.Add("@id", SqlDbType.Int).Value = userId;
            insert.Parameters.Add("@usuario", SqlDbType.VarChar, 50).Value = username;
            insert.Parameters.Add("@org", SqlDbType.Int).Value = request.Organizacion;
            insert.Parameters.Add("@cliente", SqlDbType.Int).Value = request.ClienteCodigo;
            AddCoordinate(insert, "@lat", request.Latitud);
            AddCoordinate(insert, "@lon", request.Longitud);
            id = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
        }
        if (request.Latitud.HasValue && request.Longitud.HasValue)
        {
            await using var location = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM dbo.APP_CLIENTES_UBICACION WITH (UPDLOCK,HOLDLOCK) WHERE ORGANIZACION=@org AND COD_CLIENTE=@cliente) INSERT dbo.APP_CLIENTES_UBICACION(ORGANIZACION,COD_CLIENTE,LATITUD,LONGITUD,USUARIO) VALUES(@org,@cliente,@lat,@lon,@usuario);", cn, (SqlTransaction)tx);
            location.Parameters.Add("@org", SqlDbType.Int).Value = request.Organizacion;
            location.Parameters.Add("@cliente", SqlDbType.Int).Value = request.ClienteCodigo;
            location.Parameters.Add("@usuario", SqlDbType.VarChar, 50).Value = username;
            AddCoordinate(location, "@lat", request.Latitud);
            AddCoordinate(location, "@lon", request.Longitud);
            await location.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await GetVisitByIdAsync(id, ct);
    }

    public async Task<VisitaDto?> EndVisitAsync(int userId, VisitaSalidaRequest request, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("UPDATE dbo.APP_VISITAS SET FECHA_SALIDA=GETDATE(), LAT_SALIDA=@lat, LON_SALIDA=@lon OUTPUT INSERTED.ID_VISITA WHERE ID_VISITA=(SELECT TOP (1) ID_VISITA FROM dbo.APP_VISITAS WHERE ID_USUARIO=@usuario AND FECHA_SALIDA IS NULL ORDER BY FECHA_LLEGADA DESC);", cn);
        cmd.Parameters.Add("@usuario", SqlDbType.Int).Value = userId;
        AddCoordinate(cmd, "@lat", request.Latitud);
        AddCoordinate(cmd, "@lon", request.Longitud);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? null : await GetVisitByIdAsync(Convert.ToInt32(result), ct);
    }

    public async Task<IReadOnlyList<VisitaDto>> GetVisitsAsync(DateTime? from, DateTime? to, string? seller, string? zone, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(VisitSelect + " WHERE (@desde IS NULL OR v.FECHA_LLEGADA>=@desde) AND (@hasta IS NULL OR v.FECHA_LLEGADA<DATEADD(day,1,@hasta)) AND (@vendedor IS NULL OR v.USUARIO=@vendedor) AND (@zona IS NULL OR LTRIM(RTRIM(ISNULL(c.ZONA,'')))=@zona) ORDER BY v.FECHA_LLEGADA DESC", cn);
        cmd.Parameters.Add("@desde", SqlDbType.DateTime).Value = (object?)from ?? DBNull.Value;
        cmd.Parameters.Add("@hasta", SqlDbType.DateTime).Value = (object?)to ?? DBNull.Value;
        cmd.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = string.IsNullOrWhiteSpace(seller) ? DBNull.Value : seller.Trim();
        cmd.Parameters.Add("@zona", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(zone) ? DBNull.Value : zone.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var visits = new List<VisitaDto>();
        while (await rd.ReadAsync(ct)) visits.Add(ReadVisit(rd));
        return visits;
    }

    public async Task<IReadOnlyList<ClienteUbicacionDto>> GetClientLocationsAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT u.ORGANIZACION,u.COD_CLIENTE,ISNULL(c.NOMBRE,''),ISNULL(c.ZONA,''),u.LATITUD,u.LONGITUD,u.FECHA_REGISTRO
            FROM dbo.APP_CLIENTES_UBICACION u
            OUTER APPLY (SELECT TOP (1) NOMBRE,ZONA FROM dbo.VW_APP_CLIENTES vc WHERE vc.ORGANIZACION=u.ORGANIZACION AND vc.CODIGO=u.COD_CLIENTE) c
            ORDER BY c.NOMBRE
            """, cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ClienteUbicacionDto>();
        while (await rd.ReadAsync(ct)) result.Add(new(rd.GetInt32(0), rd.GetInt32(1), rd.GetString(2).Trim(), rd.GetString(3).Trim(), Convert.ToDecimal(rd.GetValue(4)), Convert.ToDecimal(rd.GetValue(5)), rd.GetDateTime(6)));
        return result;
    }

    public async Task SaveClientLocationAsync(ClienteUbicacionRequest request, string usuario, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            UPDATE dbo.APP_CLIENTES_UBICACION
            SET LATITUD=@lat,LONGITUD=@lon,USUARIO=@usuario,FECHA_REGISTRO=GETDATE()
            WHERE ORGANIZACION=@org AND COD_CLIENTE=@cliente;
            IF @@ROWCOUNT=0
                INSERT dbo.APP_CLIENTES_UBICACION(ORGANIZACION,COD_CLIENTE,LATITUD,LONGITUD,USUARIO)
                VALUES(@org,@cliente,@lat,@lon,@usuario);
            """, cn);
        cmd.Parameters.Add("@org", SqlDbType.Int).Value = request.Organizacion;
        cmd.Parameters.Add("@cliente", SqlDbType.Int).Value = request.ClienteCodigo;
        cmd.Parameters.Add("@usuario", SqlDbType.VarChar, 50).Value = usuario;
        AddCoordinate(cmd, "@lat", request.Latitud);
        AddCoordinate(cmd, "@lon", request.Longitud);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<VisitaDto?> GetVisitByIdAsync(int id, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(VisitSelect + " WHERE v.ID_VISITA=@id", cn);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadVisit(rd) : null;
    }

    private static VisitaDto ReadVisit(SqlDataReader rd) => new(rd.GetInt32(0), rd.GetString(1).Trim(), rd.GetInt32(2), rd.GetInt32(3), rd.GetString(4).Trim(), rd.GetString(5).Trim(), rd.GetDateTime(6), rd.IsDBNull(7) ? null : rd.GetDateTime(7), rd.GetInt32(8), rd.IsDBNull(9) ? null : Convert.ToDecimal(rd.GetValue(9)), rd.IsDBNull(10) ? null : Convert.ToDecimal(rd.GetValue(10)), rd.IsDBNull(7));

    private static void AddCoordinate(SqlCommand cmd, string name, decimal? value)
    {
        var parameter = cmd.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 10;
        parameter.Scale = 7;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    public async Task<SyncResponse> GetSyncDataAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        var clientes = new List<ClienteDto>();
        var productos = new List<ProductoDto>();

        await using (var cmd = new SqlCommand("""
            SELECT c.ORGANIZACION, c.CODIGO, c.NOMBRE, c.TELEFONO, c.CUIT, c.TIPOIVA, c.ZONA
            FROM dbo.VW_APP_CLIENTES c
            WHERE CODIGO <> '-1'
            ORDER BY NOMBRE
            """, cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                clientes.Add(new(
                    Convert.ToInt32(rd.GetValue(0)),
                    Convert.ToInt32(rd.GetValue(1)),
                    rd.IsDBNull(2) ? string.Empty : Convert.ToString(rd.GetValue(2)) ?? string.Empty,
                    rd.IsDBNull(3) ? null : Convert.ToString(rd.GetValue(3)),
                    rd.IsDBNull(4) ? null : Convert.ToString(rd.GetValue(4)),
                    rd.IsDBNull(5) ? null : Convert.ToString(rd.GetValue(5)),
                    rd.IsDBNull(6) ? null : Convert.ToString(rd.GetValue(6))));

        await using (var cmd = new SqlCommand("""
            SELECT ORGANIZACION, CODART, DESCRIPCION, PRECIO, ALIC, RUBRO, FRIO
            FROM dbo.VW_APP_ARTICULOS
            WHERE ESTADO = 'A'
            ORDER BY DESCRIPCION
            """, cn))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                productos.Add(new(
                    Convert.ToInt32(rd.GetValue(0)),
                    Convert.ToInt32(rd.GetValue(1)),
                    rd.IsDBNull(2) ? string.Empty : Convert.ToString(rd.GetValue(2)) ?? string.Empty,
                    Convert.ToDecimal(rd.GetValue(3)),
                    Convert.ToDecimal(rd.GetValue(4)),
                    rd.IsDBNull(5) ? null : Convert.ToString(rd.GetValue(5)),
                    !rd.IsDBNull(6) && string.Equals(Convert.ToString(rd.GetValue(6))?.Trim(), "SI", StringComparison.OrdinalIgnoreCase)));

        return new(DateTimeOffset.UtcNow, clientes, productos);
    }

    public async Task<PedidoResponse> SaveOrderAsync(PedidoRequest pedido, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            // Entorno de prueba sobre la base real: aislar cabecera, detalle y numeración.
            const int orderOrganization = 1;
            var prefijo = pedido.Frio ? 18 : 8;
            var localDate = pedido.Fecha;
            var fecha = localDate.Year * 10000 + localDate.Month * 100 + localDate.Day;
            var hora = localDate.Hour * 10000 + localDate.Minute * 100 + localDate.Second;
            var usuario = pedido.Usuario.Trim();
            if (usuario.Length > 10) usuario = usuario[..10];
            var observa = string.IsNullOrWhiteSpace(pedido.Observacion) ? null : pedido.Observacion.Trim();
            if (observa?.Length > 50) observa = observa[..50];
            var idOrigen = string.IsNullOrWhiteSpace(pedido.IdOrigenApp) ? null : pedido.IdOrigenApp.Trim();
            if (idOrigen is not null)
            {
                await using var existing = new SqlCommand("""
                    SELECT ORGANIZACION,PREF,REMITO,ISNULL(SUB_TOTAL,0),ISNULL(IMP_RESP,0),ISNULL(IMP_TOTAL,0)
                    FROM dbo.STREMITOS WITH (UPDLOCK,HOLDLOCK)
                    WHERE ID_ORIGEN_APP=@idOrigen AND PED_USR=@usuario
                    """, cn, tx);
                existing.Parameters.Add("@idOrigen", SqlDbType.VarChar, 50).Value = idOrigen;
                existing.Parameters.Add("@usuario", SqlDbType.VarChar, 10).Value = usuario;
                await using var existingReader = await existing.ExecuteReaderAsync(ct);
                if (await existingReader.ReadAsync(ct))
                {
                    var saved = new PedidoResponse(existingReader.GetInt32(0), existingReader.GetInt32(1), existingReader.GetInt32(2), Convert.ToDecimal(existingReader.GetValue(3)), Convert.ToDecimal(existingReader.GetValue(4)), Convert.ToDecimal(existingReader.GetValue(5)));
                    await existingReader.CloseAsync();
                    await tx.CommitAsync(ct);
                    return saved;
                }
            }

            await using var nextNumber = new SqlCommand("""
                SELECT ISNULL(MAX(REMITO), 0) + 1
                FROM dbo.STREMITOS WITH (UPDLOCK, HOLDLOCK)
                WHERE ORGANIZACION=@organizacion AND PREF=@pref
                """, cn, tx);
            nextNumber.Parameters.Add("@organizacion", SqlDbType.Int).Value = orderOrganization;
            nextNumber.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
            var remito = Convert.ToInt32(await nextNumber.ExecuteScalarAsync(ct));

            var calculated = pedido.Items.Select(item =>
            {
                var preVta = Math.Round(item.Precio / (1m + item.Alic / 100m), 2, MidpointRounding.AwayFromZero);
                var subTotal = Math.Round(item.Cantidad * preVta, 2, MidpointRounding.AwayFromZero);
                var total = Math.Round(item.Cantidad * item.Precio, 2, MidpointRounding.AwayFromZero);
                var iva = total - subTotal;
                return new { Item = item, PreVta = preVta, SubTotal = subTotal, Iva = iva, Total = total };
            }).ToList();
            var subTotalPedido = calculated.Sum(x => x.SubTotal);
            var ivaPedido = calculated.Sum(x => x.Iva);
            var totalPedido = calculated.Sum(x => x.Total);

            var headerSql = idOrigen is null ? """
                INSERT INTO dbo.STREMITOS
                (ORGANIZACION, PREF, REMITO, COD_CLIENTE, COD_VEND, OBSERVA, ESTADO, FECHA,
                 SUB_TOTAL, IMP_RESP, IMP_TOTAL, AUD_FEC, AUD_HOR, AUD_USR, TIPO_REMITO,
                 DESTINO, CODBARRA, COD_ARTICULO, NROREP, MARCA, DESCRIPCION, CANTIDAD,
                 PREUNI, ALIC, ALICE, RUBSTK, LISTA, PED_USR)
                VALUES
                (@organizacion, @pref, @remito, @cliente, 2, @observa, 'R', @fecha,
                 @subtotal, @iva, @total, @fecha, @hora, 'APP', 'VE',
                 'DEP', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 1, @usuario)
                """ : """
                INSERT INTO dbo.STREMITOS
                (ORGANIZACION, PREF, REMITO, COD_CLIENTE, COD_VEND, OBSERVA, ESTADO, FECHA,
                 SUB_TOTAL, IMP_RESP, IMP_TOTAL, AUD_FEC, AUD_HOR, AUD_USR, TIPO_REMITO,
                 DESTINO, CODBARRA, COD_ARTICULO, NROREP, MARCA, DESCRIPCION, CANTIDAD,
                 PREUNI, ALIC, ALICE, RUBSTK, LISTA, PED_USR, ID_ORIGEN_APP)
                VALUES
                (@organizacion, @pref, @remito, @cliente, 2, @observa, 'R', @fecha,
                 @subtotal, @iva, @total, @fecha, @hora, 'APP', 'VE',
                 'DEP', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 1, @usuario, @idOrigen)
                """;
            await using (var header = new SqlCommand(headerSql, cn, tx))
            {
                header.Parameters.Add("@organizacion", SqlDbType.Int).Value = orderOrganization;
                header.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
                header.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
                header.Parameters.Add("@cliente", SqlDbType.Int).Value = pedido.ClienteCodigo;
                header.Parameters.Add("@observa", SqlDbType.VarChar, 50).Value = (object?)observa ?? DBNull.Value;
                header.Parameters.Add("@fecha", SqlDbType.Int).Value = fecha;
                header.Parameters.Add("@hora", SqlDbType.Int).Value = hora;
                header.Parameters.Add("@subtotal", SqlDbType.Float).Value = subTotalPedido;
                header.Parameters.Add("@iva", SqlDbType.Float).Value = ivaPedido;
                header.Parameters.Add("@total", SqlDbType.Float).Value = totalPedido;
                header.Parameters.Add("@usuario", SqlDbType.VarChar, 10).Value = usuario;
                if (idOrigen is not null) header.Parameters.Add("@idOrigen", SqlDbType.VarChar, 50).Value = idOrigen;
                await header.ExecuteNonQueryAsync(ct);
            }

            const string detailSql = """
                INSERT INTO dbo.STREMITOSDET
                (ORGANIZACION, PREF, REMITO, COD_CLIENTE, COD_ARTICULO, NROREP, MARCA,
                 FECMOV, CANTIDAD, DESCRIPCION, CANTIDEV, PREUNI, PREVTA, SUB_TOTAL,
                 ALIC, IMP_RESP, IMP_TOTAL, OBSERVA, ESTADO, FACTURA, AUD_FEC, AUD_HOR,
                 AUD_USR, NROPED, FACCON, UNIMED, TIPO_REMITO, STOCK, PED_USR)
                VALUES
                (@organizacion, @pref, @remito, @cliente, @articulo, '1', '1',
                 @fecha, @cantidad, @descripcion, 0, @preuni, @prevta, @subtotal,
                 @alic, @iva, @total, @observa, 'R', NULL, @fecha, @hora,
                 'APP', 0, 1, 'Uni', 'VE', '0', @usuario)
                """;
            foreach (var line in calculated)
            {
                var item = line.Item;
                await using var detail = new SqlCommand(detailSql, cn, tx);
                detail.Parameters.Add("@organizacion", SqlDbType.Int).Value = orderOrganization;
                detail.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
                detail.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
                detail.Parameters.Add("@cliente", SqlDbType.Int).Value = pedido.ClienteCodigo;
                detail.Parameters.Add("@articulo", SqlDbType.Int).Value = item.ProductoCodigo;
                detail.Parameters.Add("@fecha", SqlDbType.Int).Value = fecha;
                detail.Parameters.Add("@cantidad", SqlDbType.Float).Value = item.Cantidad;
                detail.Parameters.Add("@descripcion", SqlDbType.VarChar, 255).Value = item.Descripcion.Length > 255 ? item.Descripcion[..255] : item.Descripcion;
                detail.Parameters.Add("@preuni", SqlDbType.Float).Value = item.Precio;
                detail.Parameters.Add("@prevta", SqlDbType.Float).Value = line.PreVta;
                detail.Parameters.Add("@subtotal", SqlDbType.Float).Value = line.SubTotal;
                detail.Parameters.Add("@alic", SqlDbType.Float).Value = item.Alic;
                detail.Parameters.Add("@iva", SqlDbType.Float).Value = line.Iva;
                detail.Parameters.Add("@total", SqlDbType.Float).Value = line.Total;
                detail.Parameters.Add("@observa", SqlDbType.VarChar, 50).Value = (object?)observa ?? DBNull.Value;
                detail.Parameters.Add("@hora", SqlDbType.Int).Value = hora;
                detail.Parameters.Add("@usuario", SqlDbType.VarChar, 10).Value = usuario;
                await detail.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return new(orderOrganization, prefijo, remito, subTotalPedido, ivaPedido, totalPedido);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<(string Status, PedidoEdicionDto? Pedido)> BeginOrderEditAsync(int organizacion, int prefijo, int remito, int clienteCodigo, string vendedor, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var status = await GetOrderEditStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, vendedor, "R", ct);
            if (status == "locked")
                status = await GetOrderEditStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, vendedor, "X", ct);
            if (status != "ok")
            {
                await tx.RollbackAsync(ct);
                return (status, null);
            }

            await SetOrderStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, "X", ct);
            var pedido = await ReadOrderForEditAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, ct);
            await tx.CommitAsync(ct);
            return pedido is null ? ("notfound", null) : ("ok", pedido);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<string> CancelOrderEditAsync(int organizacion, int prefijo, int remito, int clienteCodigo, string vendedor, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var status = await GetOrderEditStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, vendedor, "X", ct);
            if (status == "locked")
                status = await GetOrderEditStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, vendedor, "R", ct);
            if (status != "ok")
            {
                await tx.RollbackAsync(ct);
                return status;
            }

            await SetOrderStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, "R", ct);
            await tx.CommitAsync(ct);
            return "ok";
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<(string Status, PedidoResponse? Pedido)> UpdateOrderAsync(int organizacion, int prefijo, int remito, int clienteCodigo, string vendedor, PedidoRequest pedido, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var status = await GetOrderEditStatusAsync(cn, tx, organizacion, prefijo, remito, clienteCodigo, vendedor, "X", ct);
            if (status != "ok")
            {
                await tx.RollbackAsync(ct);
                return (status, null);
            }

            await using var readDate = new SqlCommand("SELECT FECHA FROM dbo.STREMITOS WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente", cn, tx);
            AddOrderKeyParameters(readDate, organizacion, prefijo, remito, clienteCodigo);
            var fecha = Convert.ToInt32(await readDate.ExecuteScalarAsync(ct));
            var now = DateTime.Now;
            var auditDate = now.Year * 10000 + now.Month * 100 + now.Day;
            var auditTime = now.Hour * 10000 + now.Minute * 100 + now.Second;
            var usuario = vendedor.Trim();
            if (usuario.Length > 10) usuario = usuario[..10];
            var observa = string.IsNullOrWhiteSpace(pedido.Observacion) ? null : pedido.Observacion.Trim();
            if (observa?.Length > 50) observa = observa[..50];

            var calculated = pedido.Items.Select(item =>
            {
                var preVta = Math.Round(item.Precio / (1m + item.Alic / 100m), 2, MidpointRounding.AwayFromZero);
                var subTotal = Math.Round(item.Cantidad * preVta, 2, MidpointRounding.AwayFromZero);
                var total = Math.Round(item.Cantidad * item.Precio, 2, MidpointRounding.AwayFromZero);
                return new { Item = item, PreVta = preVta, SubTotal = subTotal, Iva = total - subTotal, Total = total };
            }).ToList();
            var subTotalPedido = calculated.Sum(x => x.SubTotal);
            var ivaPedido = calculated.Sum(x => x.Iva);
            var totalPedido = calculated.Sum(x => x.Total);

            await using (var deleteDetail = new SqlCommand("DELETE dbo.STREMITOSDET WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente", cn, tx))
            {
                AddOrderKeyParameters(deleteDetail, organizacion, prefijo, remito, clienteCodigo);
                await deleteDetail.ExecuteNonQueryAsync(ct);
            }

            const string detailSql = """
                INSERT INTO dbo.STREMITOSDET
                (ORGANIZACION, PREF, REMITO, COD_CLIENTE, COD_ARTICULO, NROREP, MARCA,
                 FECMOV, CANTIDAD, DESCRIPCION, CANTIDEV, PREUNI, PREVTA, SUB_TOTAL,
                 ALIC, IMP_RESP, IMP_TOTAL, OBSERVA, ESTADO, FACTURA, AUD_FEC, AUD_HOR,
                 AUD_USR, NROPED, FACCON, UNIMED, TIPO_REMITO, STOCK, PED_USR)
                VALUES
                (@org, @pref, @remito, @cliente, @articulo, '1', '1',
                 @fecha, @cantidad, @descripcion, 0, @preuni, @prevta, @subtotal,
                 @alic, @iva, @total, @observa, 'R', NULL, @auditDate, @auditTime,
                 'APP', 0, 1, 'Uni', 'VE', '0', @usuario)
                """;
            foreach (var line in calculated)
            {
                await using var detail = new SqlCommand(detailSql, cn, tx);
                AddOrderKeyParameters(detail, organizacion, prefijo, remito, clienteCodigo);
                detail.Parameters.Add("@articulo", SqlDbType.Int).Value = line.Item.ProductoCodigo;
                detail.Parameters.Add("@fecha", SqlDbType.Int).Value = fecha;
                detail.Parameters.Add("@cantidad", SqlDbType.Float).Value = line.Item.Cantidad;
                detail.Parameters.Add("@descripcion", SqlDbType.VarChar, 255).Value = line.Item.Descripcion.Length > 255 ? line.Item.Descripcion[..255] : line.Item.Descripcion;
                detail.Parameters.Add("@preuni", SqlDbType.Float).Value = line.Item.Precio;
                detail.Parameters.Add("@prevta", SqlDbType.Float).Value = line.PreVta;
                detail.Parameters.Add("@subtotal", SqlDbType.Float).Value = line.SubTotal;
                detail.Parameters.Add("@alic", SqlDbType.Float).Value = line.Item.Alic;
                detail.Parameters.Add("@iva", SqlDbType.Float).Value = line.Iva;
                detail.Parameters.Add("@total", SqlDbType.Float).Value = line.Total;
                detail.Parameters.Add("@observa", SqlDbType.VarChar, 50).Value = (object?)observa ?? DBNull.Value;
                detail.Parameters.Add("@auditDate", SqlDbType.Int).Value = auditDate;
                detail.Parameters.Add("@auditTime", SqlDbType.Int).Value = auditTime;
                detail.Parameters.Add("@usuario", SqlDbType.VarChar, 10).Value = usuario;
                await detail.ExecuteNonQueryAsync(ct);
            }

            await using (var updateHeader = new SqlCommand("""
                UPDATE dbo.STREMITOS
                SET OBSERVA=@observa, SUB_TOTAL=@subtotal, IMP_RESP=@iva, IMP_TOTAL=@total,
                    ESTADO='R', AUD_FEC=@auditDate, AUD_HOR=@auditTime, AUD_USR='APP'
                WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
                  AND PED_USR=@vendedor AND ESTADO='X'
                """, cn, tx))
            {
                AddOrderKeyParameters(updateHeader, organizacion, prefijo, remito, clienteCodigo);
                updateHeader.Parameters.Add("@observa", SqlDbType.VarChar, 50).Value = (object?)observa ?? DBNull.Value;
                updateHeader.Parameters.Add("@subtotal", SqlDbType.Float).Value = subTotalPedido;
                updateHeader.Parameters.Add("@iva", SqlDbType.Float).Value = ivaPedido;
                updateHeader.Parameters.Add("@total", SqlDbType.Float).Value = totalPedido;
                updateHeader.Parameters.Add("@auditDate", SqlDbType.Int).Value = auditDate;
                updateHeader.Parameters.Add("@auditTime", SqlDbType.Int).Value = auditTime;
                updateHeader.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = vendedor;
                if (await updateHeader.ExecuteNonQueryAsync(ct) != 1)
                {
                    await tx.RollbackAsync(ct);
                    return ("locked", null);
                }
            }

            await tx.CommitAsync(ct);
            return ("ok", new(organizacion, prefijo, remito, subTotalPedido, ivaPedido, totalPedido));
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> GetOrderEditStatusAsync(SqlConnection cn, SqlTransaction tx, int organizacion, int prefijo, int remito, int clienteCodigo, string vendedor, string? requiredState, CancellationToken ct)
    {
        await using var check = new SqlCommand("""
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM dbo.STREMITOS WITH (UPDLOCK,HOLDLOCK)
                                 WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente) THEN 'notfound'
                WHEN NOT EXISTS (SELECT 1 FROM dbo.STREMITOS
                                 WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente AND PED_USR=@vendedor) THEN 'forbidden'
                WHEN EXISTS (SELECT 1 FROM dbo.STREMITOSDET
                             WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
                               AND LTRIM(RTRIM(ISNULL(FACTURA,'')))<>'') THEN 'billed'
                WHEN @requiredState IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.STREMITOS
                             WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
                               AND ISNULL(ESTADO,'')<>@requiredState) THEN 'locked'
                WHEN @requiredState IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.STREMITOSDET
                             WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
                               AND ISNULL(ESTADO,'')<>@requiredState) THEN 'locked'
                ELSE 'ok' END
            """, cn, tx);
        AddOrderKeyParameters(check, organizacion, prefijo, remito, clienteCodigo);
        check.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = vendedor;
        check.Parameters.Add("@requiredState", SqlDbType.VarChar, 1).Value = (object?)requiredState ?? DBNull.Value;
        return Convert.ToString(await check.ExecuteScalarAsync(ct)) ?? "notfound";
    }

    private static async Task SetOrderStatusAsync(SqlConnection cn, SqlTransaction tx, int organizacion, int prefijo, int remito, int clienteCodigo, string status, CancellationToken ct)
    {
        await using var detail = new SqlCommand("UPDATE dbo.STREMITOSDET SET ESTADO=@status WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente", cn, tx);
        AddOrderKeyParameters(detail, organizacion, prefijo, remito, clienteCodigo);
        detail.Parameters.Add("@status", SqlDbType.VarChar, 1).Value = status;
        await detail.ExecuteNonQueryAsync(ct);
        await using var header = new SqlCommand("UPDATE dbo.STREMITOS SET ESTADO=@status WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente", cn, tx);
        AddOrderKeyParameters(header, organizacion, prefijo, remito, clienteCodigo);
        header.Parameters.Add("@status", SqlDbType.VarChar, 1).Value = status;
        await header.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PedidoEdicionDto?> ReadOrderForEditAsync(SqlConnection cn, SqlTransaction tx, int organizacion, int prefijo, int remito, int clienteCodigo, CancellationToken ct)
    {
        int fecha;
        string observa;
        await using (var header = new SqlCommand("SELECT FECHA,ISNULL(OBSERVA,'') FROM dbo.STREMITOS WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente", cn, tx))
        {
            AddOrderKeyParameters(header, organizacion, prefijo, remito, clienteCodigo);
            await using var rd = await header.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;
            fecha = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0));
            observa = rd.IsDBNull(1) ? string.Empty : Convert.ToString(rd.GetValue(1))?.Trim() ?? string.Empty;
        }
        var items = new List<PedidoDetalleDto>();
        await using (var detail = new SqlCommand("SELECT COD_ARTICULO,ISNULL(DESCRIPCION,''),ISNULL(CANTIDAD,0),ISNULL(PREUNI,0),ISNULL(ALIC,0),ISNULL(IMP_TOTAL,0) FROM dbo.STREMITOSDET WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente ORDER BY NRO_ID", cn, tx))
        {
            AddOrderKeyParameters(detail, organizacion, prefijo, remito, clienteCodigo);
            await using var rd = await detail.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new(Convert.ToInt32(rd.GetValue(0)), Convert.ToString(rd.GetValue(1))?.Trim() ?? string.Empty, Convert.ToDecimal(rd.GetValue(2)), Convert.ToDecimal(rd.GetValue(3)), Convert.ToDecimal(rd.GetValue(4)), Convert.ToDecimal(rd.GetValue(5))));
        }
        return new(organizacion, prefijo, remito, clienteCodigo, fecha, prefijo == 18, observa, items);
    }

    private static void AddOrderKeyParameters(SqlCommand cmd, int organizacion, int prefijo, int remito, int clienteCodigo)
    {
        cmd.Parameters.Add("@org", SqlDbType.Int).Value = organizacion;
        cmd.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
        cmd.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
        cmd.Parameters.Add("@cliente", SqlDbType.Int).Value = clienteCodigo;
    }

    public async Task<PedidoConsultaResponse> GetOrdersAsync(int? desde, int? hasta, string? vendedor, string? cliente, string? zona, int pagina, int tamanoPagina, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamanoPagina = tamanoPagina is 10 or 25 or 50 ? tamanoPagina : 10;
        var desdeFila = (pagina - 1) * tamanoPagina;
        var hastaFila = desdeFila + tamanoPagina;
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            WITH Pedidos AS
            (
                SELECT r.ORGANIZACION, r.PREF, r.REMITO, r.COD_CLIENTE,
                       COALESCE(c.NOMBRE, '') AS CLIENTE_NOMBRE,
                       r.FECHA, r.IMP_TOTAL, COALESCE(u.NOMBRE, r.PED_USR, '') AS VENDEDOR,
                       CASE WHEN r.PREF = 18 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS FRIO,
                       COALESCE(d.FACTURA, '') AS FACTURA,
                       COALESCE(r.ESTADO, '') AS ESTADO,
                       ROW_NUMBER() OVER (ORDER BY r.FECHA DESC, r.REMITO DESC) AS FILA,
                       COUNT(*) OVER () AS TOTAL
                FROM dbo.STREMITOS r
                LEFT JOIN dbo.APP_USUARIOS u ON u.USUARIO = r.PED_USR
                OUTER APPLY
                (
                    SELECT TOP (1) NOMBRE, ZONA
                    FROM dbo.VW_APP_CLIENTES vc
                    WHERE vc.ORGANIZACION = r.ORGANIZACION AND vc.CODIGO = r.COD_CLIENTE
                ) c
                OUTER APPLY
                (
                    SELECT MAX(sd.FACTURA) AS FACTURA
                    FROM dbo.STREMITOSDET sd
                    WHERE sd.ORGANIZACION = r.ORGANIZACION AND sd.PREF = r.PREF
                      AND sd.REMITO = r.REMITO AND sd.COD_CLIENTE = r.COD_CLIENTE
                ) d
                WHERE r.PREF IN (8, 18)
                  AND (@desde IS NULL OR r.FECHA >= @desde)
                  AND (@hasta IS NULL OR r.FECHA <= @hasta)
                  AND (@vendedor IS NULL OR r.PED_USR = @vendedor)
                  AND (@cliente IS NULL OR CONVERT(varchar(20),r.COD_CLIENTE) = @cliente OR c.NOMBRE LIKE '%' + @cliente + '%')
                  AND (@zona IS NULL OR c.ZONA = @zona)
            )
            SELECT ORGANIZACION,PREF,REMITO,COD_CLIENTE,CLIENTE_NOMBRE,FECHA,IMP_TOTAL,VENDEDOR,FRIO,FACTURA,ESTADO,TOTAL
            FROM Pedidos
            WHERE FILA > @desdeFila AND FILA <= @hastaFila
            ORDER BY FILA
            """, cn);
        cmd.CommandTimeout = 180;
        cmd.Parameters.Add("@desde", SqlDbType.Int).Value = (object?)desde ?? DBNull.Value;
        cmd.Parameters.Add("@hasta", SqlDbType.Int).Value = (object?)hasta ?? DBNull.Value;
        cmd.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = string.IsNullOrWhiteSpace(vendedor) ? DBNull.Value : vendedor.Trim();
        cmd.Parameters.Add("@cliente", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(cliente) ? DBNull.Value : cliente.Trim();
        cmd.Parameters.Add("@zona", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(zona) ? DBNull.Value : zona.Trim();
        cmd.Parameters.Add("@desdeFila", SqlDbType.Int).Value = desdeFila;
        cmd.Parameters.Add("@hastaFila", SqlDbType.Int).Value = hastaFila;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PedidoConsultaDto>();
        var total = 0;
        while (await rd.ReadAsync(ct))
        {
            result.Add(new(rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3), rd.IsDBNull(4) ? string.Empty : Convert.ToString(rd.GetValue(4)) ?? string.Empty, rd.IsDBNull(5) ? 0 : rd.GetInt32(5), rd.IsDBNull(6) ? 0 : Convert.ToDecimal(rd.GetValue(6)), rd.IsDBNull(7) ? string.Empty : rd.GetString(7).Trim(), rd.GetBoolean(8), rd.IsDBNull(9) ? string.Empty : Convert.ToString(rd.GetValue(9)) ?? string.Empty, rd.IsDBNull(10) ? string.Empty : Convert.ToString(rd.GetValue(10))?.Trim() ?? string.Empty));
            total = Convert.ToInt32(rd.GetValue(11));
        }
        return new(result, total, pagina, tamanoPagina);
    }

    public async Task<string> DeleteOwnUnbilledOrderAsync(int organizacion, int prefijo, int remito, int clienteCodigo, string vendedor, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var check = new SqlCommand("""
                SELECT CASE
                    WHEN NOT EXISTS (SELECT 1 FROM dbo.STREMITOS WITH (UPDLOCK,HOLDLOCK)
                                     WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente) THEN 'notfound'
                    WHEN NOT EXISTS (SELECT 1 FROM dbo.STREMITOS
                                     WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente AND PED_USR=@vendedor) THEN 'forbidden'
                    WHEN EXISTS (SELECT 1 FROM dbo.STREMITOSDET
                                 WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
                                   AND LTRIM(RTRIM(ISNULL(FACTURA,'')))<>'') THEN 'billed'
                    WHEN EXISTS (SELECT 1 FROM dbo.STREMITOS
                                 WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
                                   AND ISNULL(ESTADO,'')<>'R') THEN 'locked'
                    ELSE 'ok' END
                """, cn, (SqlTransaction)tx);
            check.Parameters.Add("@org", SqlDbType.Int).Value = organizacion;
            check.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
            check.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
            check.Parameters.Add("@cliente", SqlDbType.Int).Value = clienteCodigo;
            check.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = vendedor;
            var status = Convert.ToString(await check.ExecuteScalarAsync(ct)) ?? "notfound";
            if (status != "ok")
            {
                await tx.RollbackAsync(ct);
                return status;
            }

            await using var deleteDetail = new SqlCommand("DELETE dbo.STREMITOSDET WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente", cn, (SqlTransaction)tx);
            deleteDetail.Parameters.Add("@org", SqlDbType.Int).Value = organizacion;
            deleteDetail.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
            deleteDetail.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
            deleteDetail.Parameters.Add("@cliente", SqlDbType.Int).Value = clienteCodigo;
            await deleteDetail.ExecuteNonQueryAsync(ct);

            await using var deleteHeader = new SqlCommand("DELETE dbo.STREMITOS WHERE ORGANIZACION=@org AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente AND PED_USR=@vendedor", cn, (SqlTransaction)tx);
            deleteHeader.Parameters.Add("@org", SqlDbType.Int).Value = organizacion;
            deleteHeader.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
            deleteHeader.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
            deleteHeader.Parameters.Add("@cliente", SqlDbType.Int).Value = clienteCodigo;
            deleteHeader.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = vendedor;
            if (await deleteHeader.ExecuteNonQueryAsync(ct) != 1)
            {
                await tx.RollbackAsync(ct);
                return "notfound";
            }
            await tx.CommitAsync(ct);
            return "ok";
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<VendedorFiltroDto>> GetSellersAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT USUARIO,NOMBRE FROM dbo.APP_USUARIOS WHERE ROL='V' AND HABILITADO=1 ORDER BY NOMBRE", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<VendedorFiltroDto>();
        while (await rd.ReadAsync(ct)) result.Add(new(rd.GetString(0).Trim(), rd.GetString(1).Trim()));
        return result;
    }

    public async Task<IReadOnlyList<string>> GetZonesAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT DISTINCT LTRIM(RTRIM(ZONA)) AS ZONA
            FROM dbo.VW_APP_CLIENTES
            WHERE ZONA IS NOT NULL AND LTRIM(RTRIM(ZONA)) <> ''
            ORDER BY ZONA
            """, cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await rd.ReadAsync(ct)) result.Add(Convert.ToString(rd.GetValue(0)) ?? string.Empty);
        return result;
    }

    public async Task<CatalogosClienteDto> GetClientCatalogsAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        return new(
            await ReadLookupAsync(cn, "GSTIPOIVA", ["TIPIVA", "IVA", "TIPOIVA", "TIPO_IVA", "CODIGO", "COD_IVA"], ["DETALLE", "DESCRIP", "DESCRIPCION", "NOMBRE", "DENOMINACION"], null, ct),
            await ReadLookupAsync(cn, "EXPROVINCIA", ["PCIA", "CODIGO", "COD_PROV", "COD_PROVINCIA"], ["PROVINCIA", "DETALLE", "DESCRIP", "DESCRIPCION", "NOMBRE", "DENOMINACION"], null, ct),
            await ReadLookupAsync(cn, "EXLOCALIDAD", ["LOCALI", "CODIGO", "COD_LOC", "COD_LOCALIDAD"], ["LOCALIDAD", "DETALLE", "DESCRIP", "DESCRIPCION", "NOMBRE", "DENOMINACION"], ["PCIA", "PROVINCIA", "COD_PROV", "COD_PROVINCIA"], ct),
            await ReadLookupAsync(cn, "GSZONA", ["ZONA", "CODIGO", "COD_ZONA"], ["DETALLE", "DESCRIP", "DESCRIPCION", "NOMBRE", "DENOMINACION"], null, ct),
            await ReadLookupAsync(cn, "TATIPDOC", ["CAEDOC", "TIPDOC", "TIPODOC", "CODIGO", "COD_DOC"], ["DETALLE", "DESCRIP", "DESCRIPCION", "NOMBRE", "DENOMINACION"], null, ct),
            await ReadClientTypesAsync(cn, ct));
    }

    public async Task<IReadOnlyList<ClienteGestionDto>> SearchManagedClientsAsync(string? texto, CancellationToken ct)
    {
        texto = texto?.Trim();
        if (string.IsNullOrWhiteSpace(texto)) return [];
        var isCode = int.TryParse(texto, out var code);
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT TOP (30) ORGANIZACION,COD_CLIENTE,ISNULL(NOMBRE,''),ISNULL(IVA,''),ISNULL(CUIT,''),
                   ISNULL(DIRECCION,''),ISNULL(POSTAL,''),ISNULL(PCIA,''),ISNULL(LOCALI,''),ISNULL(CELULAR,''),
                   ISNULL(EMAIL,''),ISNULL(PAGINAWEB,''),ISNULL(OBSERVACIONES,''),ISNULL(ZONA,0),ISNULL(CAEDOC,''),ISNULL(EXENTO,''),ISNULL(TIPCLI,'A')
            FROM dbo.FUCLIENTES
            WHERE ORGANIZACION=1
              AND ((@esCodigo=1 AND COD_CLIENTE=@codigo) OR (@esCodigo=0 AND NOMBRE LIKE '%' + @texto + '%'))
            ORDER BY NOMBRE,COD_CLIENTE
            """, cn);
        cmd.Parameters.Add("@esCodigo", SqlDbType.Bit).Value = isCode;
        cmd.Parameters.Add("@codigo", SqlDbType.Int).Value = isCode ? code : -1;
        cmd.Parameters.Add("@texto", SqlDbType.VarChar, 120).Value = texto;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ClienteGestionDto>();
        while (await rd.ReadAsync(ct)) result.Add(ReadManagedClient(rd));
        return result;
    }

    public async Task<ClienteGestionDto> SaveManagedClientAsync(ClienteGestionRequest request, string username, CancellationToken ct)
    {
        var name = (request.Nombre ?? string.Empty).Trim();
        var address = (request.Direccion ?? string.Empty).Trim();
        var iva = DefaultText(request.Iva, "F");
        var cuit = DefaultText(request.Cuit, "00000000000").Replace("-", string.Empty).Replace(" ", string.Empty);
        var postal = DefaultText(request.Postal, "6237");
        var province = DefaultText(request.Provincia, "01");
        var locality = DefaultText(request.Localidad, "094");
        var documentType = DefaultText(request.TipoDocumento, "99");
        var exempt = DefaultText(request.Exento, "No");
        var clientType = DefaultText(request.TipoCliente, "A");
        var zone = request.Zona ?? 10;
        if (name.Length == 0 || name.Length > 120) throw new InvalidOperationException("Ingrese el nombre del cliente.");
        if (address.Length == 0 || address.Length > 255) throw new InvalidOperationException("Ingrese el domicilio del cliente.");
        if (!ValidCuit(cuit)) throw new InvalidOperationException("El CUIT ingresado no es válido.");
        if ((request.Celular?.Length ?? 0) > 50 || (request.Email?.Length ?? 0) > 250 || (request.PaginaWeb?.Length ?? 0) > 50 || (request.Observaciones?.Length ?? 0) > 100)
            throw new InvalidOperationException("Uno de los textos supera la longitud permitida.");

        var catalogs = await GetClientCatalogsAsync(ct);
        if (!catalogs.TiposIva.Any(x => x.Codigo.Equals(iva, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Seleccione un tipo de IVA válido.");
        if (!catalogs.Provincias.Any(x => x.Codigo.Equals(province, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Seleccione una provincia válida.");
        if (!catalogs.Localidades.Any(x => x.Codigo.Equals(locality, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(x.Padre) || x.Padre.Equals(province, StringComparison.OrdinalIgnoreCase)))) throw new InvalidOperationException("Seleccione una localidad válida para la provincia.");
        if (!catalogs.Zonas.Any(x => x.Codigo == zone.ToString())) throw new InvalidOperationException("Seleccione una zona válida.");
        if (!catalogs.TiposDocumento.Any(x => x.Codigo.Equals(documentType, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Seleccione un tipo de documento válido.");
        if (!catalogs.TiposCliente.Any(x => x.Codigo.Equals(clientType, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Seleccione un tipo de cliente válido.");

        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        using var tx = cn.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            if (cuit != "00000000000")
            {
                await using var duplicate = new SqlCommand("SELECT COUNT(*) FROM dbo.FUCLIENTES WHERE CUIT=@cuit AND NOT (ORGANIZACION=1 AND COD_CLIENTE=@codigo)", cn, tx);
                duplicate.Parameters.Add("@cuit", SqlDbType.VarChar, 11).Value = cuit;
                duplicate.Parameters.Add("@codigo", SqlDbType.Int).Value = request.Codigo ?? -1;
                if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(ct)) > 0) throw new InvalidOperationException("El CUIT ya está registrado en otro cliente.");
            }

            var code = request.Codigo;
            if (code is null)
            {
                await using var next = new SqlCommand("SELECT CONVERT(int,ISNULL(MAX(COD_CLIENTE),0))+1 FROM dbo.FUCLIENTES WITH (UPDLOCK,HOLDLOCK) WHERE ORGANIZACION=1", cn, tx);
                code = Convert.ToInt32(await next.ExecuteScalarAsync(ct));
                if (code > short.MaxValue) throw new InvalidOperationException("No hay códigos de cliente disponibles.");
                await using var insert = new SqlCommand("""
                    INSERT dbo.FUCLIENTES
                    (ORGANIZACION,COD_CLIENTE,NOMBRE,IVA,CUIT,DIRECCION,POSTAL,PCIA,LOCALI,CELULAR,EMAIL,PAGINAWEB,
                     SALDO_INI,FECHA_INI,SALDO_ANT,OBSERVACIONES,CONDIB,CONDGN,TIPCLI,GRPCLI,ZONA,COD_VEND,DESCTO,LISTA,
                     AUD_FEC,AUD_HOR,AUD_USR,CAEDOC,EXENTO)
                    VALUES
                    (1,@codigo,@nombre,@iva,@cuit,@direccion,@postal,@pcia,@locali,@celular,@email,@web,
                     0,CONVERT(int,CONVERT(char(8),GETDATE(),112)),0,@observaciones,'1','1',@tipcli,0,@zona,1,1,1,
                     CONVERT(int,CONVERT(char(8),GETDATE(),112)),CONVERT(int,REPLACE(CONVERT(char(8),GETDATE(),108),':','')),@usuario,@documento,@exento)
                    """, cn, tx);
                AddManagedClientParameters(insert, code.Value, name, iva, cuit, address, postal, province, locality, request, zone, username, documentType, exempt, clientType);
                await insert.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var update = new SqlCommand("""
                    UPDATE dbo.FUCLIENTES SET NOMBRE=@nombre,IVA=@iva,CUIT=@cuit,DIRECCION=@direccion,POSTAL=@postal,
                        PCIA=@pcia,LOCALI=@locali,CELULAR=@celular,EMAIL=@email,PAGINAWEB=@web,OBSERVACIONES=@observaciones,
                        ZONA=@zona,CAEDOC=@documento,EXENTO=@exento,TIPCLI=@tipcli,
                        AUD_FEC=CONVERT(int,CONVERT(char(8),GETDATE(),112)),
                        AUD_HOR=CONVERT(int,REPLACE(CONVERT(char(8),GETDATE(),108),':','')),AUD_USR=@usuario
                    WHERE ORGANIZACION=1 AND COD_CLIENTE=@codigo
                    """, cn, tx);
                AddManagedClientParameters(update, code.Value, name, iva, cuit, address, postal, province, locality, request, zone, username, documentType, exempt, clientType);
                if (await update.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException("El cliente que intenta modificar ya no existe.");
            }
            tx.Commit();
            return new(1, code.Value, name, iva, cuit, address, postal, province, locality, request.Celular?.Trim() ?? string.Empty, request.Email?.Trim() ?? string.Empty, request.PaginaWeb?.Trim() ?? string.Empty, request.Observaciones?.Trim() ?? string.Empty, zone, documentType, exempt, clientType);
        }
        catch { tx.Rollback(); throw; }
    }

    private static void AddManagedClientParameters(SqlCommand cmd, int code, string name, string iva, string cuit, string address, string postal, string province, string locality, ClienteGestionRequest request, int zone, string username, string documentType, string exempt, string clientType)
    {
        cmd.Parameters.Add("@codigo", SqlDbType.Int).Value = code;
        cmd.Parameters.Add("@nombre", SqlDbType.VarChar, 120).Value = name;
        cmd.Parameters.Add("@iva", SqlDbType.VarChar, 2).Value = iva;
        cmd.Parameters.Add("@cuit", SqlDbType.VarChar, 11).Value = cuit;
        cmd.Parameters.Add("@direccion", SqlDbType.VarChar, 255).Value = address;
        cmd.Parameters.Add("@postal", SqlDbType.VarChar, 12).Value = postal;
        cmd.Parameters.Add("@pcia", SqlDbType.VarChar, 2).Value = province;
        cmd.Parameters.Add("@locali", SqlDbType.VarChar, 3).Value = locality;
        cmd.Parameters.Add("@celular", SqlDbType.VarChar, 50).Value = (object?)request.Celular?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@email", SqlDbType.VarChar, 250).Value = (object?)request.Email?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@web", SqlDbType.VarChar, 50).Value = (object?)request.PaginaWeb?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@observaciones", SqlDbType.VarChar, 100).Value = (object?)request.Observaciones?.Trim() ?? DBNull.Value;
        cmd.Parameters.Add("@zona", SqlDbType.Int).Value = zone;
        cmd.Parameters.Add("@usuario", SqlDbType.VarChar, 10).Value = string.IsNullOrWhiteSpace(username) ? "APP" : username.Trim()[..Math.Min(10, username.Trim().Length)];
        cmd.Parameters.Add("@documento", SqlDbType.VarChar, 2).Value = documentType;
        cmd.Parameters.Add("@exento", SqlDbType.VarChar, 2).Value = exempt;
        cmd.Parameters.Add("@tipcli", SqlDbType.VarChar, 2).Value = clientType;
    }

    private static ClienteGestionDto ReadManagedClient(SqlDataReader rd) => new(
        Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)), rd.GetString(2).Trim(), rd.GetString(3).Trim(), rd.GetString(4).Trim(), rd.GetString(5).Trim(), rd.GetString(6).Trim(), rd.GetString(7).Trim(), rd.GetString(8).Trim(), rd.GetString(9).Trim(), rd.GetString(10).Trim(), rd.GetString(11).Trim(), rd.GetString(12).Trim(), Convert.ToInt32(rd.GetValue(13)), rd.GetString(14).Trim(), rd.GetString(15).Trim(), rd.GetString(16).Trim());

    private static async Task<IReadOnlyList<OpcionClienteDto>> ReadClientTypesAsync(SqlConnection cn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT CONVERT(varchar(50),TIPCLI),ISNULL(CONVERT(varchar(200),DETALLE),CONVERT(varchar(50),TIPCLI)) FROM dbo.GSTIPOCLI WHERE ORGANIZACION=1 ORDER BY DETALLE", cn);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<OpcionClienteDto>();
        while (await rd.ReadAsync(ct)) result.Add(new(rd.GetString(0).Trim(), rd.GetString(1).Trim()));
        return result;
    }

    private static string DefaultText(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool ValidCuit(string value)
    {
        if (value == "00000000000") return true;
        if (value.Length != 11 || value.Any(x => x is < '0' or > '9')) return false;
        int[] weights = [5,4,3,2,7,6,5,4,3,2];
        var sum = 0;
        for (var i = 0; i < weights.Length; i++) sum += (value[i] - '0') * weights[i];
        var digit = 11 - sum % 11;
        if (digit == 11) digit = 0; else if (digit == 10) digit = 9;
        return digit == value[10] - '0';
    }

    private static async Task<IReadOnlyList<OpcionClienteDto>> ReadLookupAsync(SqlConnection cn, string table, string[] codeCandidates, string[] descriptionCandidates, string[]? parentCandidates, CancellationToken ct)
    {
        await using var columns = new SqlCommand("SELECT name FROM sys.columns WHERE object_id=OBJECT_ID(@table)", cn);
        columns.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = "dbo." + table;
        await using var rd = await columns.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await rd.ReadAsync(ct)) names.Add(rd.GetString(0));
        await rd.CloseAsync();
        if (names.Count == 0) throw new InvalidOperationException($"No existe la tabla de validación {table}.");
        string Pick(string[] candidates, bool required = true)
        {
            var found = candidates.Select(candidate => names.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(name => name is not null);
            if (found is null && required) throw new InvalidOperationException($"No se reconocen las columnas de {table}.");
            return found ?? string.Empty;
        }
        var code = Pick(codeCandidates);
        var description = Pick(descriptionCandidates, false);
        var parent = parentCandidates is null ? string.Empty : Pick(parentCandidates, false);
        var descriptionSql = description.Length == 0 ? $"CONVERT(varchar(200),[{code}])" : $"ISNULL(CONVERT(varchar(200),[{description}]),CONVERT(varchar(200),[{code}]))";
        var parentSql = parent.Length == 0 ? "NULL" : $"CONVERT(varchar(50),[{parent}])";
        await using var query = new SqlCommand($"SELECT DISTINCT CONVERT(varchar(50),[{code}]),{descriptionSql},{parentSql} FROM dbo.[{table}] WHERE [{code}] IS NOT NULL ORDER BY 2", cn);
        await using var values = await query.ExecuteReaderAsync(ct);
        var result = new List<OpcionClienteDto>();
        while (await values.ReadAsync(ct)) result.Add(new(Convert.ToString(values.GetValue(0))?.Trim() ?? string.Empty, Convert.ToString(values.GetValue(1))?.Trim() ?? string.Empty, values.IsDBNull(2) ? null : Convert.ToString(values.GetValue(2))?.Trim()));
        return result;
    }

    public async Task<IReadOnlyList<ClienteDto>> SearchClientsAsync(string? texto, string? zona, CancellationToken ct)
    {
        texto = texto?.Trim();
        if (string.IsNullOrWhiteSpace(texto)) return [];
        var isCode = int.TryParse(texto, out var codigo);
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT TOP (30) ORGANIZACION, CODIGO, ISNULL(NOMBRE,''), TELEFONO, CUIT, TIPOIVA, ZONA
            FROM dbo.VW_APP_CLIENTES
            WHERE (@zona IS NULL OR LTRIM(RTRIM(ZONA)) = @zona)
              AND ((@esCodigo = 1 AND CODIGO = @codigo)
                   OR (@esCodigo = 0 AND NOMBRE LIKE '%' + @texto + '%'))
            ORDER BY NOMBRE, ORGANIZACION, CODIGO
            """, cn);
        cmd.Parameters.Add("@zona", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(zona) ? DBNull.Value : zona.Trim();
        cmd.Parameters.Add("@esCodigo", SqlDbType.Bit).Value = isCode;
        cmd.Parameters.Add("@codigo", SqlDbType.Int).Value = isCode ? codigo : -1;
        cmd.Parameters.Add("@texto", SqlDbType.VarChar, 100).Value = texto;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ClienteDto>();
        while (await rd.ReadAsync(ct))
            result.Add(new(Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)), rd.GetString(2).Trim(), rd.IsDBNull(3) ? null : Convert.ToString(rd.GetValue(3))?.Trim(), rd.IsDBNull(4) ? null : Convert.ToString(rd.GetValue(4))?.Trim(), rd.IsDBNull(5) ? null : Convert.ToString(rd.GetValue(5))?.Trim(), rd.IsDBNull(6) ? null : Convert.ToString(rd.GetValue(6))?.Trim()));
        return result;
    }

    public async Task<ClientePercepcionDto> GetClientPerceptionAsync(int organizacion, int codigo, CancellationToken ct)
    {
        if (!tenant.PercepIibb) return new(0);
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(c.IVA,'')))) IN ('I','M','E')
                        THEN ISNULL((SELECT TOP (1) pp.ALIC FROM dbo.PADRONPERCE pp WHERE pp.CUIT=c.CUIT),8)
                        ELSE 0 END
            FROM dbo.VW_APP_CLIENTES c
            WHERE c.ORGANIZACION=@organizacion AND c.CODIGO=@codigo
            """, cn);
        cmd.Parameters.Add("@organizacion", SqlDbType.Int).Value = organizacion;
        cmd.Parameters.Add("@codigo", SqlDbType.Int).Value = codigo;
        var value = await cmd.ExecuteScalarAsync(ct);
        return new(value is null or DBNull ? 0 : Convert.ToDecimal(value));
    }

    public async Task<IReadOnlyList<PedidoDetalleDto>> GetOrderDetailAsync(int organizacion, int prefijo, int remito, int clienteCodigo, string? vendedorUsuario, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT COD_ARTICULO, COALESCE(DESCRIPCION, ''), CANTIDAD, PREUNI, ALIC, IMP_TOTAL
            FROM dbo.STREMITOSDET
            WHERE ORGANIZACION = @organizacion AND PREF = @pref AND REMITO = @remito AND COD_CLIENTE = @cliente
              AND (@vendedor IS NULL OR EXISTS
                  (SELECT 1 FROM dbo.STREMITOS r
                   WHERE r.ORGANIZACION=@organizacion AND r.PREF=@pref AND r.REMITO=@remito
                     AND r.COD_CLIENTE=@cliente AND r.PED_USR=@vendedor))
            ORDER BY NRO_ID
            """, cn);
        cmd.Parameters.Add("@organizacion", SqlDbType.Int).Value = organizacion;
        cmd.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
        cmd.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
        cmd.Parameters.Add("@cliente", SqlDbType.Int).Value = clienteCodigo;
        cmd.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = string.IsNullOrWhiteSpace(vendedorUsuario) ? DBNull.Value : vendedorUsuario.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PedidoDetalleDto>();
        while (await rd.ReadAsync(ct))
            result.Add(new(rd.GetInt32(0), rd.GetString(1), Convert.ToDecimal(rd.GetValue(2)), Convert.ToDecimal(rd.GetValue(3)), Convert.ToDecimal(rd.GetValue(4)), Convert.ToDecimal(rd.GetValue(5))));
        return result;
    }

    public async Task<PedidoPdfData?> GetOrderPdfDataAsync(int organizacion, int prefijo, int remito, int clienteCodigo, string? vendedorUsuario, CancellationToken ct)
    {
        var percepcionHabilitada = tenant.PercepIibb;
        var percepcionCampo = percepcionHabilitada
            ? "CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(c.IVA,'')))) IN ('I','M','E') THEN ISNULL(p.ALIC,8) ELSE 0 END"
            : "CAST(0 AS float)";
        var percepcionOrigen = percepcionHabilitada
            ? "OUTER APPLY (SELECT TOP (1) ALIC FROM dbo.PADRONPERCE pp WHERE LTRIM(RTRIM(pp.CUIT))=LTRIM(RTRIM(c.CUIT))) p"
            : string.Empty;
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            SELECT r.FECHA,ISNULL(r.AUD_HOR,0),ISNULL(c.NOMBRE,''),ISNULL(c.DIRECCION,''),ISNULL(c.ZONA,''),
                   ISNULL(c.CUIT,''),ISNULL(c.TIPOIVA,''),ISNULL(u.NOMBRE,ISNULL(r.PED_USR,'')),
                   ISNULL(t.TITULO,'Voy Pidiendo'),ISNULL(t.DIRECCION,''),ISNULL(t.CUIT,''),ISNULL(t.COMENTARIO,''),
                   {percepcionCampo}
            FROM dbo.STREMITOS r
            LEFT JOIN dbo.APP_USUARIOS u ON u.USUARIO=r.PED_USR
            OUTER APPLY (SELECT TOP (1) NOMBRE,DIRECCION,ZONA,CUIT,TIPOIVA,IVA FROM dbo.VW_APP_CLIENTES vc WHERE vc.ORGANIZACION=r.ORGANIZACION AND vc.CODIGO=r.COD_CLIENTE) c
            {percepcionOrigen}
            OUTER APPLY (SELECT TOP (1) TITULO,DIRECCION,CUIT,COMENTARIO FROM dbo.APP_TITULO) t
            WHERE r.ORGANIZACION=@organizacion AND r.PREF=@pref AND r.REMITO=@remito AND r.COD_CLIENTE=@cliente
              AND (@vendedor IS NULL OR r.PED_USR=@vendedor);

            SELECT ISNULL(COD_ARTICULO,0),ISNULL(CANTIDAD,0),ISNULL(DESCRIPCION,''),ISNULL(PREUNI,0),ISNULL(IMP_TOTAL,0)
            FROM dbo.STREMITOSDET
            WHERE ORGANIZACION=@organizacion AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente
            ORDER BY NRO_ID;

            SELECT ISNULL(SUM(SUB_TOTAL),0),ISNULL(SUM(IMP_RESP),0),ISNULL(SUM(IMP_TOTAL),0)
            FROM dbo.STREMITOSDET
            WHERE ORGANIZACION=@organizacion AND PREF=@pref AND REMITO=@remito AND COD_CLIENTE=@cliente;
            """, cn);
        cmd.Parameters.Add("@organizacion", SqlDbType.Int).Value = organizacion;
        cmd.Parameters.Add("@pref", SqlDbType.Int).Value = prefijo;
        cmd.Parameters.Add("@remito", SqlDbType.Int).Value = remito;
        cmd.Parameters.Add("@cliente", SqlDbType.Int).Value = clienteCodigo;
        cmd.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = string.IsNullOrWhiteSpace(vendedorUsuario) ? DBNull.Value : vendedorUsuario.Trim();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var fecha = rd.IsDBNull(0) ? 0 : Convert.ToInt32(rd.GetValue(0));
        var hora = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1));
        var cliente = Convert.ToString(rd.GetValue(2))?.Trim() ?? string.Empty;
        var domicilio = Convert.ToString(rd.GetValue(3))?.Trim() ?? string.Empty;
        var localidad = Convert.ToString(rd.GetValue(4))?.Trim() ?? string.Empty;
        var clienteCuit = Convert.ToString(rd.GetValue(5))?.Trim() ?? string.Empty;
        var clienteTipoIva = Convert.ToString(rd.GetValue(6))?.Trim() ?? string.Empty;
        var vendedor = Convert.ToString(rd.GetValue(7))?.Trim() ?? string.Empty;
        var empresa = Convert.ToString(rd.GetValue(8))?.Trim() ?? "Voy Pidiendo";
        var empresaDireccion = Convert.ToString(rd.GetValue(9))?.Trim() ?? string.Empty;
        var empresaCuit = Convert.ToString(rd.GetValue(10))?.Trim() ?? string.Empty;
        var comentario = Convert.ToString(rd.GetValue(11))?.Trim() ?? string.Empty;
        var percepcionAlicuota = Convert.ToDecimal(rd.GetValue(12));
        var lineas = new List<PedidoPdfLinea>();
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
            lineas.Add(new(Convert.ToInt32(rd.GetValue(0)), Convert.ToDecimal(rd.GetValue(1)), Convert.ToString(rd.GetValue(2))?.Trim() ?? string.Empty, Convert.ToDecimal(rd.GetValue(3)), Convert.ToDecimal(rd.GetValue(4))));
        await rd.NextResultAsync(ct);
        await rd.ReadAsync(ct);
        var subTotal = Convert.ToDecimal(rd.GetValue(0));
        var iva = Convert.ToDecimal(rd.GetValue(1));
        var total = Convert.ToDecimal(rd.GetValue(2));
        var percepcionIibb = Math.Round(subTotal * percepcionAlicuota / 100m, 2, MidpointRounding.AwayFromZero);
        return new(organizacion, prefijo, remito, fecha, hora, cliente, domicilio, localidad, clienteCuit, clienteTipoIva, vendedor, empresa, empresaDireccion, empresaCuit, comentario, subTotal, iva, percepcionAlicuota, percepcionIibb, total + percepcionIibb, lineas);
    }

    public async Task<DashboardResponse> GetDashboardAsync(int? desde, int? hasta, string? zona, string? cliente, string? vendedor, string? marca, string? rubro, CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT r.ORGANIZACION, r.PREF, r.REMITO, r.COD_CLIENTE, r.FECHA,
                   ISNULL(r.IMP_TOTAL, 0) AS TOTAL_PEDIDO, COALESCE(u.NOMBRE, r.PED_USR, '') AS USUARIO,
                   ISNULL(c.NOMBRE, '') AS CLIENTE, ISNULL(c.ZONA, '') AS ZONA,
                   d.NRO_ID, ISNULL(d.IMP_TOTAL, 0) AS TOTAL_RENGLON,
                   ISNULL(a.RUBRO, 'SIN RUBRO') AS RUBRO,
                   ISNULL(NULLIF(LTRIM(RTRIM(a.MARCA)), ''), 'SIN MARCA') AS GRUPO,
                   d.COD_ARTICULO, ISNULL(d.DESCRIPCION, '') AS DESCRIPCION,
                   ISNULL(d.CANTIDAD, 0) AS CANTIDAD
            FROM dbo.STREMITOS r
            LEFT JOIN dbo.APP_USUARIOS u ON u.USUARIO = r.PED_USR
            LEFT JOIN dbo.STREMITOSDET d
              ON d.ORGANIZACION=r.ORGANIZACION AND d.PREF=r.PREF AND d.REMITO=r.REMITO AND d.COD_CLIENTE=r.COD_CLIENTE
            OUTER APPLY
            (
                SELECT TOP (1) NOMBRE, ZONA
                FROM dbo.VW_APP_CLIENTES vc
                WHERE vc.ORGANIZACION = r.ORGANIZACION
                  AND LTRIM(RTRIM(CONVERT(varchar(50), vc.CODIGO))) = CONVERT(varchar(50), r.COD_CLIENTE)
            ) c
            OUTER APPLY
            (
                SELECT TOP (1) RUBRO, MARCA
                FROM dbo.VW_APP_ARTICULOS va
                WHERE va.ORGANIZACION = d.ORGANIZACION
                  AND LTRIM(RTRIM(CONVERT(varchar(50), va.CODART))) = CONVERT(varchar(50), d.COD_ARTICULO)
                ORDER BY va.ORGANIZACION
            ) a
            WHERE r.PREF IN (8,18)
              AND (@desde IS NULL OR r.FECHA >= @desde)
              AND (@hasta IS NULL OR r.FECHA <= @hasta)
              AND (@zona IS NULL OR c.ZONA = @zona)
              AND (@cliente IS NULL OR CONVERT(varchar(20), r.COD_CLIENTE) LIKE '%' + @cliente + '%' OR c.NOMBRE LIKE '%' + @cliente + '%')
              AND (@vendedor IS NULL OR r.PED_USR = @vendedor)
              AND (@marca IS NULL OR LTRIM(RTRIM(a.MARCA)) = @marca)
              AND (@rubro IS NULL OR LTRIM(RTRIM(a.RUBRO)) = @rubro)
            """, cn);
        cmd.Parameters.Add("@desde", SqlDbType.Int).Value = (object?)desde ?? DBNull.Value;
        cmd.Parameters.Add("@hasta", SqlDbType.Int).Value = (object?)hasta ?? DBNull.Value;
        cmd.Parameters.Add("@zona", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(zona) ? DBNull.Value : zona.Trim();
        cmd.Parameters.Add("@cliente", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(cliente) ? DBNull.Value : cliente.Trim();
        cmd.Parameters.Add("@vendedor", SqlDbType.VarChar, 50).Value = string.IsNullOrWhiteSpace(vendedor) ? DBNull.Value : vendedor.Trim();
        cmd.Parameters.Add("@marca", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(marca) ? DBNull.Value : marca.Trim();
        cmd.Parameters.Add("@rubro", SqlDbType.VarChar, 200).Value = string.IsNullOrWhiteSpace(rubro) ? DBNull.Value : rubro.Trim();

        using var sqlLog = AppFileLog.BeginSql("Dashboard Pedidos", cmd);
        var rows = new List<DashboardRow>();
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
                rows.Add(new(rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3), rd.IsDBNull(4) ? 0 : rd.GetInt32(4), Convert.ToDecimal(rd.GetValue(5)), rd.GetString(6).Trim(), rd.GetString(7).Trim(), rd.GetString(8).Trim(), rd.IsDBNull(9) ? null : rd.GetInt32(9), Convert.ToDecimal(rd.GetValue(10)), rd.GetString(11).Trim(), rd.GetString(12).Trim(), rd.IsDBNull(13) ? null : rd.GetInt32(13), rd.GetString(14).Trim(), Convert.ToDecimal(rd.GetValue(15))));

        var detailFilter = !string.IsNullOrWhiteSpace(marca) || !string.IsNullOrWhiteSpace(rubro);
        var orders = rows.GroupBy(x => new { x.Org, x.Pref, x.Remito, x.Cliente })
            .Select(g => detailFilter ? g.First() with { TotalPedido = g.Sum(x => x.TotalLinea) } : g.First()).ToList();
        var sale = orders.Sum(x => x.TotalPedido);
        var orderCount = orders.Count;
        List<DashboardCategoriaDto> Category(IEnumerable<IGrouping<string, DashboardRow>> groups) =>
            groups.Select(g => new DashboardCategoriaDto(string.IsNullOrWhiteSpace(g.Key) ? "SIN DATOS" : g.Key, g.Sum(x => x.TotalPedido), g.Count())).OrderByDescending(x => x.Total).ToList();

        var bySeller = Category(orders.GroupBy(x => x.Usuario));
        var byCold = Category(orders.GroupBy(x => x.Pref == 18 ? "FRÍO" : "SIN FRÍO"));
        var byZone = Category(orders.GroupBy(x => x.Zona));
        var byClient = Category(orders.GroupBy(x => string.IsNullOrWhiteSpace(x.ClienteNombre) ? x.Cliente.ToString() : x.ClienteNombre));
        var byDate = orders.GroupBy(x => x.Fecha).Select(g => new DashboardFechaDto(g.Key, g.Sum(x => x.TotalPedido), g.Count())).OrderBy(x => x.Fecha).ToList();
        var byCategory = rows.Where(x => x.IdLinea.HasValue).GroupBy(x => string.IsNullOrWhiteSpace(x.Rubro) ? "SIN RUBRO" : x.Rubro)
            .Select(g => new DashboardCategoriaDto(g.Key, g.Sum(x => x.TotalLinea), g.Select(x => new { x.Org, x.Pref, x.Remito, x.Cliente }).Distinct().Count()))
            .OrderByDescending(x => x.Total).ToList();
        var byGroup = rows.Where(x => x.IdLinea.HasValue).GroupBy(x => string.IsNullOrWhiteSpace(x.Grupo) ? "SIN MARCA" : x.Grupo)
            .Select(g => new DashboardCategoriaDto(g.Key, g.Sum(x => x.TotalLinea), g.Select(x => new { x.Org, x.Pref, x.Remito, x.Cliente }).Distinct().Count()))
            .OrderByDescending(x => x.Total).ToList();
        var byArticleQuantity = rows.Where(x => x.Articulo.HasValue)
            .GroupBy(x => new { Codigo = x.Articulo!.Value, x.Descripcion })
            .Select(g => new DashboardArticuloDto(g.Key.Codigo, g.Key.Descripcion, g.Sum(x => x.Cantidad)))
            .OrderByDescending(x => x.Cantidad).Take(20).ToList();
        var byArticleAmount = rows.Where(x => x.Articulo.HasValue)
            .GroupBy(x => x.Articulo!.Value)
            .Select(g => new DashboardCategoriaDto(
                $"{g.Key} - {g.Select(x => x.Descripcion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty}",
                g.Sum(x => x.TotalLinea),
                g.Select(x => new { x.Org, x.Pref, x.Remito, x.Cliente }).Distinct().Count()))
            .OrderByDescending(x => x.Total).Take(20).ToList();

        sqlLog.Complete();
        return new(sale, orderCount, orders.Select(x => new { x.Org, x.Cliente }).Distinct().Count(), orderCount == 0 ? 0 : sale / orderCount, byDate, bySeller, byCategory, byGroup, byCold, byZone, byClient, byArticleAmount, byArticleQuantity);
    }

    public async Task<FacturacionDashboardResponse> GetFacturacionDashboardAsync(int? desde, int? hasta, int opcion, string? zona, string? cliente, string? marca, string? rubro, int? articulo, CancellationToken ct)
    {
        opcion = opcion is 1 or 2 or 3 ? opcion : 1;
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT v.FECMOV, LTRIM(RTRIM(v.TIPO_COMP)), v.COD_CLI, ISNULL(v.NOMBRE, ''),
                   CASE WHEN @filtraArticulo = 1 THEN ISNULL(fd.TOTAL,0) ELSE ISNULL(v.IMP_TOTAL,0) END
            FROM dbo.GSIVAVTA v
            OUTER APPLY
            (
                SELECT TOP (1) vc.ZONA
                FROM dbo.VW_APP_CLIENTES vc
                WHERE vc.ORGANIZACION = v.ORGANIZACION AND vc.CODIGO = v.COD_CLI
            ) c
            OUTER APPLY
            (
                SELECT SUM(ISNULL(d.IMP_TOTAL,0)) AS TOTAL, COUNT(*) AS CANTIDAD
                FROM dbo.GSIVADET d
                OUTER APPLY
                (
                    SELECT TOP (1) va.MARCA, va.RUBRO
                    FROM dbo.VW_APP_ARTICULOS va
                    WHERE va.ORGANIZACION = d.ORGANIZACION AND va.CODART = d.COD_ARTICULO
                ) a
                WHERE d.ORGANIZACION = v.ORGANIZACION AND d.EMPRESA = v.EMPRESA
                  AND d.TIPO_COMP = v.TIPO_COMP AND d.LETRA = v.LETRA
                  AND d.PREF = v.PREF AND d.NUMERO = v.NUMERO
                  AND (@marca IS NULL OR LTRIM(RTRIM(a.MARCA)) = @marca)
                  AND (@rubro IS NULL OR LTRIM(RTRIM(a.RUBRO)) = @rubro)
                  AND (@articulo IS NULL OR d.COD_ARTICULO = @articulo)
            ) fd
            WHERE v.TIPO_COMP IN ('FA','ND','PR','NC','DE')
              AND (@desde IS NULL OR v.FECMOV >= @desde)
              AND (@hasta IS NULL OR v.FECMOV <= @hasta)
              AND (@zona IS NULL OR LTRIM(RTRIM(c.ZONA)) = @zona)
              AND (@cliente IS NULL OR CONVERT(varchar(20),v.COD_CLI) = @cliente OR v.NOMBRE LIKE '%' + @cliente + '%')
              AND (@filtraArticulo = 0 OR fd.CANTIDAD > 0)
              AND (@opcion = 3
                   OR (@opcion = 1 AND UPPER(LTRIM(RTRIM(v.IVA))) IN (N'SÍ','SI'))
                   OR (@opcion = 2 AND UPPER(LTRIM(RTRIM(v.IVA))) = 'NO'));

            SELECT LTRIM(RTRIM(v.TIPO_COMP)), d.COD_ARTICULO, ISNULL(a.DESCRIPCION, ISNULL(d.DETALLE, '')),
                   ISNULL(NULLIF(LTRIM(RTRIM(a.MARCA)), ''), 'SIN MARCA'),
                   ISNULL(NULLIF(LTRIM(RTRIM(a.RUBRO)), ''), 'SIN RUBRO'),
                   ISNULL(d.IMP_TOTAL, 0), ISNULL(d.CANTIDAD, 0)
            FROM dbo.GSIVAVTA v
            INNER JOIN dbo.GSIVADET d
              ON d.ORGANIZACION = v.ORGANIZACION AND d.EMPRESA = v.EMPRESA
             AND d.TIPO_COMP = v.TIPO_COMP AND d.LETRA = v.LETRA
             AND d.PREF = v.PREF AND d.NUMERO = v.NUMERO
            OUTER APPLY
            (
                SELECT TOP (1) va.MARCA, va.RUBRO, va.DESCRIPCION
                FROM dbo.VW_APP_ARTICULOS va
                WHERE va.ORGANIZACION = d.ORGANIZACION AND va.CODART = d.COD_ARTICULO
            ) a
            OUTER APPLY
            (
                SELECT TOP (1) vc.ZONA
                FROM dbo.VW_APP_CLIENTES vc
                WHERE vc.ORGANIZACION = v.ORGANIZACION AND vc.CODIGO = v.COD_CLI
            ) c
            WHERE v.TIPO_COMP IN ('FA','ND','PR','NC','DE')
              AND (@desde IS NULL OR v.FECMOV >= @desde)
              AND (@hasta IS NULL OR v.FECMOV <= @hasta)
              AND (@zona IS NULL OR LTRIM(RTRIM(c.ZONA)) = @zona)
              AND (@cliente IS NULL OR CONVERT(varchar(20),v.COD_CLI) = @cliente OR v.NOMBRE LIKE '%' + @cliente + '%')
              AND (@marca IS NULL OR LTRIM(RTRIM(a.MARCA)) = @marca)
              AND (@rubro IS NULL OR LTRIM(RTRIM(a.RUBRO)) = @rubro)
              AND (@articulo IS NULL OR d.COD_ARTICULO = @articulo)
              AND (@opcion = 3
                   OR (@opcion = 1 AND UPPER(LTRIM(RTRIM(v.IVA))) IN (N'SÍ','SI'))
                   OR (@opcion = 2 AND UPPER(LTRIM(RTRIM(v.IVA))) = 'NO'));
            """, cn);
        cmd.CommandTimeout = 180;
        cmd.Parameters.Add("@desde", SqlDbType.Int).Value = (object?)desde ?? DBNull.Value;
        cmd.Parameters.Add("@hasta", SqlDbType.Int).Value = (object?)hasta ?? DBNull.Value;
        cmd.Parameters.Add("@opcion", SqlDbType.Int).Value = opcion;
        cmd.Parameters.Add("@zona", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(zona) ? DBNull.Value : zona.Trim();
        cmd.Parameters.Add("@cliente", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(cliente) ? DBNull.Value : cliente.Trim();
        cmd.Parameters.Add("@marca", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(marca) ? DBNull.Value : marca.Trim();
        cmd.Parameters.Add("@rubro", SqlDbType.VarChar, 200).Value = string.IsNullOrWhiteSpace(rubro) ? DBNull.Value : rubro.Trim();
        cmd.Parameters.Add("@articulo", SqlDbType.Int).Value = (object?)articulo ?? DBNull.Value;
        cmd.Parameters.Add("@filtraArticulo", SqlDbType.Bit).Value = !string.IsNullOrWhiteSpace(marca) || !string.IsNullOrWhiteSpace(rubro) || articulo.HasValue;

        using var sqlLog = AppFileLog.BeginSql("Ventas Facturadas", cmd);
        var headers = new List<FacturacionHeaderRow>();
        var details = new List<FacturacionDetailRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var tipo = rd.GetString(1).Trim();
            var signo = tipo is "NC" or "DE" ? -1m : 1m;
            headers.Add(new(rd.IsDBNull(0) ? 0 : rd.GetInt32(0), tipo, rd.IsDBNull(2) ? 0 : rd.GetInt32(2), rd.GetString(3).Trim(), signo * Math.Abs(Convert.ToDecimal(rd.GetValue(4)))));
        }
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var tipo = rd.GetString(0).Trim();
            var signo = tipo is "NC" or "DE" ? -1m : 1m;
            details.Add(new(tipo, rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetString(2).Trim(), rd.GetString(3).Trim(), rd.GetString(4).Trim(), signo * Math.Abs(Convert.ToDecimal(rd.GetValue(5))), signo * Math.Abs(Convert.ToDecimal(rd.GetValue(6)))));
        }

        var ventas = headers.Where(x => x.Total >= 0).Sum(x => x.Total);
        var devoluciones = Math.Abs(headers.Where(x => x.Total < 0).Sum(x => x.Total));
        var neta = ventas - devoluciones;
        List<DashboardCategoriaDto> Categories(IEnumerable<IGrouping<string, FacturacionHeaderRow>> groups) =>
            groups.Select(g => new DashboardCategoriaDto(string.IsNullOrWhiteSpace(g.Key) ? "SIN DATOS" : g.Key, g.Sum(x => x.Total), g.Count())).OrderByDescending(x => x.Total).ToList();
        var porFecha = headers.GroupBy(x => x.Fecha).Select(g => new DashboardFechaDto(g.Key, g.Sum(x => x.Total), g.Count())).OrderBy(x => x.Fecha).ToList();
        var porCliente = Categories(headers.GroupBy(x => string.IsNullOrWhiteSpace(x.Nombre) ? x.Cliente.ToString() : x.Nombre));
        var porMarca = details.GroupBy(x => string.IsNullOrWhiteSpace(x.Marca) ? "SIN MARCA" : x.Marca)
            .Select(g => new DashboardCategoriaDto(g.Key, g.Sum(x => x.Total), g.Count())).OrderByDescending(x => x.Total).ToList();
        var porRubro = details.GroupBy(x => string.IsNullOrWhiteSpace(x.Rubro) ? "SIN RUBRO" : x.Rubro)
            .Select(g => new DashboardCategoriaDto(g.Key, g.Sum(x => x.Total), g.Count())).OrderByDescending(x => x.Total).ToList();
        var porArticuloImporte = details.Where(x => x.Articulo.HasValue).GroupBy(x => x.Articulo!.Value)
            .Select(g => new DashboardCategoriaDto($"{g.Key} - {g.Select(x => x.Descripcion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "SIN DESCRIPCIÓN"}", g.Sum(x => x.Total), g.Count())).OrderByDescending(x => x.Total).Take(20).ToList();
        var porArticuloCantidad = details.Where(x => x.Articulo.HasValue).GroupBy(x => x.Articulo!.Value)
            .Select(g => new DashboardArticuloDto(g.Key, g.Select(x => x.Descripcion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "SIN DESCRIPCIÓN", g.Sum(x => x.Cantidad))).OrderByDescending(x => x.Cantidad).Take(20).ToList();
        var comprobantesVenta = headers.Count(x => x.Total >= 0);
        sqlLog.Complete();
        return new(neta, ventas, devoluciones, headers.Count, headers.Select(x => x.Cliente).Distinct().Count(), comprobantesVenta == 0 ? 0 : ventas / comprobantesVenta, porFecha, porCliente, porMarca, porRubro, porArticuloImporte, porArticuloCantidad);
    }

    public async Task<FacturacionFiltrosDto> GetBillingFiltersAsync(CancellationToken ct)
    {
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT DISTINCT LTRIM(RTRIM(MARCA)) FROM dbo.VW_APP_ARTICULOS WHERE MARCA IS NOT NULL AND LTRIM(RTRIM(MARCA))<>'' ORDER BY 1;
            SELECT DISTINCT LTRIM(RTRIM(RUBRO)) FROM dbo.VW_APP_ARTICULOS WHERE RUBRO IS NOT NULL AND LTRIM(RTRIM(RUBRO))<>'' ORDER BY 1;
            """, cn);
        var marcas = new List<string>();
        var rubros = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) marcas.Add(Convert.ToString(rd.GetValue(0)) ?? string.Empty);
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct)) rubros.Add(Convert.ToString(rd.GetValue(0)) ?? string.Empty);
        return new(marcas, rubros);
    }

    public async Task<IReadOnlyList<ArticuloFiltroDto>> SearchArticlesAsync(string? texto, string? marca, string? rubro, CancellationToken ct)
    {
        texto = texto?.Trim();
        if (string.IsNullOrWhiteSpace(texto)) return [];
        var isCode = int.TryParse(texto, out var codigo);
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT TOP (30) ORGANIZACION,CODART,ISNULL(DESCRIPCION,'')
            FROM dbo.VW_APP_ARTICULOS
            WHERE (@marca IS NULL OR LTRIM(RTRIM(MARCA))=@marca)
              AND (@rubro IS NULL OR LTRIM(RTRIM(RUBRO))=@rubro)
              AND ((@esCodigo=1 AND CODART=@codigo) OR (@esCodigo=0 AND DESCRIPCION LIKE '%' + @texto + '%'))
            ORDER BY DESCRIPCION,ORGANIZACION,CODART
            """, cn);
        cmd.Parameters.Add("@marca", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(marca) ? DBNull.Value : marca.Trim();
        cmd.Parameters.Add("@rubro", SqlDbType.VarChar, 200).Value = string.IsNullOrWhiteSpace(rubro) ? DBNull.Value : rubro.Trim();
        cmd.Parameters.Add("@esCodigo", SqlDbType.Bit).Value = isCode;
        cmd.Parameters.Add("@codigo", SqlDbType.Int).Value = isCode ? codigo : -1;
        cmd.Parameters.Add("@texto", SqlDbType.VarChar, 200).Value = texto;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<ArticuloFiltroDto>();
        while (await rd.ReadAsync(ct)) result.Add(new(Convert.ToInt32(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1)), Convert.ToString(rd.GetValue(2))?.Trim() ?? string.Empty));
        return result;
    }

    public async Task<AnualDashboardResponse> GetAnnualDashboardAsync(int anio, int opcion, string? zona, string? cliente, string? marca, string? rubro, int? articulo, int limite, string? modo, CancellationToken ct)
    {
        anio = Math.Clamp(anio, 2020, 2030);
        opcion = opcion is 1 or 2 or 3 ? opcion : 1;
        limite = limite is 10 or 20 or 40 or 50 or 100 ? limite : 20;
        var porImporte = string.Equals(modo, "pesos", StringComparison.OrdinalIgnoreCase);
        var desde = anio * 10000 + 101;
        var hasta = anio * 10000 + 1231;
        await using var cn = CreateConnection();
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT ISNULL(v.FECMOV,0),LTRIM(RTRIM(v.TIPO_COMP)),ISNULL(v.COD_CLI,0),ISNULL(v.NOMBRE,''),
                   ISNULL(d.COD_ARTICULO,0),ISNULL(a.DESCRIPCION,ISNULL(d.DETALLE,'')),
                   ISNULL(NULLIF(LTRIM(RTRIM(a.MARCA)),''),'SIN MARCA'),
                   ISNULL(NULLIF(LTRIM(RTRIM(a.RUBRO)),''),'SIN RUBRO'),
                   ISNULL(d.IMP_TOTAL,0),ISNULL(d.CANTIDAD,0)
            FROM dbo.GSIVAVTA v
            INNER JOIN dbo.GSIVADET d
              ON d.ORGANIZACION=v.ORGANIZACION AND d.EMPRESA=v.EMPRESA
             AND d.TIPO_COMP=v.TIPO_COMP AND d.LETRA=v.LETRA
             AND d.PREF=v.PREF AND d.NUMERO=v.NUMERO
            OUTER APPLY
            (
                SELECT TOP (1) va.DESCRIPCION,va.MARCA,va.RUBRO
                FROM dbo.VW_APP_ARTICULOS va
                WHERE va.ORGANIZACION=d.ORGANIZACION AND va.CODART=d.COD_ARTICULO
            ) a
            OUTER APPLY
            (
                SELECT TOP (1) vc.ZONA
                FROM dbo.VW_APP_CLIENTES vc
                WHERE vc.ORGANIZACION=v.ORGANIZACION AND vc.CODIGO=v.COD_CLI
            ) c
            WHERE v.TIPO_COMP IN ('FA','ND','PR','NC','DE')
              AND v.FECMOV BETWEEN @desde AND @hasta
              AND (@zona IS NULL OR LTRIM(RTRIM(c.ZONA))=@zona)
              AND (@cliente IS NULL OR CONVERT(varchar(20),v.COD_CLI)=@cliente OR v.NOMBRE LIKE '%' + @cliente + '%')
              AND (@marca IS NULL OR LTRIM(RTRIM(a.MARCA))=@marca)
              AND (@rubro IS NULL OR LTRIM(RTRIM(a.RUBRO))=@rubro)
              AND (@articulo IS NULL OR d.COD_ARTICULO=@articulo)
              AND (@opcion=3
                   OR (@opcion=1 AND UPPER(LTRIM(RTRIM(v.IVA))) IN (N'SÍ','SI'))
                   OR (@opcion=2 AND UPPER(LTRIM(RTRIM(v.IVA)))='NO'))
            """, cn);
        cmd.CommandTimeout = 300;
        cmd.Parameters.Add("@desde", SqlDbType.Int).Value = desde;
        cmd.Parameters.Add("@hasta", SqlDbType.Int).Value = hasta;
        cmd.Parameters.Add("@opcion", SqlDbType.Int).Value = opcion;
        cmd.Parameters.Add("@zona", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(zona) ? DBNull.Value : zona.Trim();
        cmd.Parameters.Add("@cliente", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(cliente) ? DBNull.Value : cliente.Trim();
        cmd.Parameters.Add("@marca", SqlDbType.VarChar, 100).Value = string.IsNullOrWhiteSpace(marca) ? DBNull.Value : marca.Trim();
        cmd.Parameters.Add("@rubro", SqlDbType.VarChar, 200).Value = string.IsNullOrWhiteSpace(rubro) ? DBNull.Value : rubro.Trim();
        cmd.Parameters.Add("@articulo", SqlDbType.Int).Value = (object?)articulo ?? DBNull.Value;
        using var sqlLog = AppFileLog.BeginSql(porImporte ? "Ventas Anuales (Pesos)" : "Ventas Anuales (Cantidades)", cmd);
        var rows = new List<AnualDetailRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var tipo = rd.GetString(1).Trim();
            var signo = tipo is "NC" or "DE" ? -1m : 1m;
            rows.Add(new(Convert.ToInt32(rd.GetValue(0)), tipo, Convert.ToInt32(rd.GetValue(2)), rd.GetString(3).Trim(), Convert.ToInt32(rd.GetValue(4)), rd.GetString(5).Trim(), rd.GetString(6).Trim(), rd.GetString(7).Trim(), signo * Math.Abs(Convert.ToDecimal(rd.GetValue(8))), signo * Math.Abs(Convert.ToDecimal(rd.GetValue(9)))));
        }

        static int Mes(int fecha) => fecha > 0 ? fecha / 100 % 100 : 0;
        decimal Valor(AnualDetailRow x) => porImporte ? x.Total : x.Cantidad;
        List<AnualFilaDto> Tabla(IEnumerable<IGrouping<string, AnualDetailRow>> grupos) => grupos
            .Select(g => new AnualFilaDto(g.Key, g.Sum(Valor), Enumerable.Range(1, 12).Select(m => g.Where(x => Mes(x.Fecha) == m).Sum(Valor)).ToList()))
            .OrderByDescending(x => x.TotalAnual).Take(limite).ToList();
        var evolucion = Enumerable.Range(1, 12).Select(m => new DashboardFechaDto(anio * 10000 + m * 100 + 1, rows.Where(x => Mes(x.Fecha) == m).Sum(x => x.Total), rows.Where(x => Mes(x.Fecha) == m).Select(x => new { x.Tipo, x.Fecha, x.Cliente }).Distinct().Count())).ToList();
        var porMarca = Tabla(rows.GroupBy(x => x.Marca));
        var porRubro = Tabla(rows.GroupBy(x => x.Rubro));
        var porArticulo = rows.GroupBy(x => x.Articulo)
            .Select(g => new AnualFilaDto(
                $"{g.Key} - {g.Select(x => x.Descripcion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty}",
                g.Sum(Valor),
                Enumerable.Range(1, 12).Select(m => g.Where(x => Mes(x.Fecha) == m).Sum(Valor)).ToList()))
            .OrderByDescending(x => x.TotalAnual).Take(limite).ToList();
        var porCliente = Tabla(rows.GroupBy(x => string.IsNullOrWhiteSpace(x.ClienteNombre) ? x.Cliente.ToString() : x.ClienteNombre));
        sqlLog.Complete();
        return new(evolucion, porMarca, porRubro, porArticulo, porCliente);
    }
}

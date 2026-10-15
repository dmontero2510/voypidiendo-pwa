/*
   Contrato de lectura para la aplicación móvil.
   Ajustar los nombres de las columnas de origen si su base usa otros nombres.
*/

IF OBJECT_ID('dbo.VW_APP_CLIENTES', 'V') IS NOT NULL
    DROP VIEW dbo.VW_APP_CLIENTES;
GO

CREATE VIEW dbo.VW_APP_CLIENTES
AS
    SELECT
        CAST(ORGANIZACION AS int)         AS ORGANIZACION,
        CAST(CODIGO AS varchar(50))       AS CODIGO,
        CAST(NOMBRE AS varchar(200))      AS NOMBRE,
        CAST(TELEFONO AS varchar(100))    AS TELEFONO,
        CAST(CUIT AS varchar(20))         AS CUIT,
        CAST(TIPOIVA AS varchar(100))     AS TIPOIVA,
        CAST(ZONA AS varchar(100))        AS ZONA
    FROM dbo.CLIENTES;
GO

IF OBJECT_ID('dbo.VW_APP_ARTICULOS', 'V') IS NOT NULL
    DROP VIEW dbo.VW_APP_ARTICULOS;
GO

CREATE VIEW dbo.VW_APP_ARTICULOS
AS
    SELECT
        CAST(ORGANIZACION AS int)         AS ORGANIZACION,
        CAST(CODART AS varchar(50))       AS CODART,
        CAST(DESCRIPCION AS varchar(200)) AS DESCRIPCION,
        CAST(PRECIO AS decimal(18, 2))    AS PRECIO,
        CAST(ALIC AS decimal(4, 1))       AS ALIC,
        CAST(RUBRO AS varchar(200))       AS RUBRO,
        CAST(FRIO AS varchar(2))          AS FRIO
    FROM dbo.PRODUCTOS
    ;
GO

/*
   Recomendado: el usuario utilizado por la API sólo necesita SELECT sobre estas
   vistas y permisos de INSERT/SELECT sobre las tablas de pedidos.

   GRANT SELECT ON dbo.VW_APP_CLIENTES TO UsuarioApi;
   GRANT SELECT ON dbo.VW_APP_ARTICULOS TO UsuarioApi;
   GRANT SELECT, INSERT ON dbo.STREMITOS TO UsuarioApi;
   GRANT INSERT ON dbo.STREMITOSDET TO UsuarioApi;
*/

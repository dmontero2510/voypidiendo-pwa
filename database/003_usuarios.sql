IF OBJECT_ID('dbo.APP_USUARIOS', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.APP_USUARIOS
    (
        ID_USUARIO       int IDENTITY(1,1) NOT NULL,
        USUARIO          varchar(50) NOT NULL,
        NOMBRE           varchar(100) NOT NULL,
        CLAVE_HASH       varchar(100) NOT NULL,
        ROL              char(1) NOT NULL,
        HABILITADO       bit NOT NULL CONSTRAINT DF_APP_USUARIOS_HABILITADO DEFAULT (1),
        COD_VEND         int NULL, /* Para usuarios V de Voy Pidiendo se utiliza 2 (venta ambulante). */
        FECHA_ALTA       datetime2(0) NOT NULL CONSTRAINT DF_APP_USUARIOS_FECHA_ALTA DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_APP_USUARIOS PRIMARY KEY (ID_USUARIO),
        CONSTRAINT UX_APP_USUARIOS_USUARIO UNIQUE (USUARIO),
        CONSTRAINT CK_APP_USUARIOS_ROL CHECK (ROL IN ('A', 'V', 'P', 'S', 'Q'))
    );
END;
GO

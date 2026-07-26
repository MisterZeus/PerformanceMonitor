/*
Runs on: ag1 (primary)

Creates the database master key in master and the certificate that both mirroring
endpoints authenticate with, then exports the certificate and its private key so
setup.ps1 can copy them to ag2.

This is the shared-certificate form of the recipe: one certificate, owned by dbo on
both replicas, used as the AUTHENTICATION certificate by both endpoints. Because dbo
in master maps to sa, no extra endpoint login or GRANT CONNECT is needed.

Requires sqlcmd variable: cert_password
*/
USE master;
GO

IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.symmetric_keys AS sk
    WHERE sk.name = N'##MS_DatabaseMasterKey##'
)
BEGIN
    CREATE MASTER KEY
        ENCRYPTION BY PASSWORD = '$(cert_password)';
END;
GO

IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.certificates AS c
    WHERE c.name = N'ag_fixture_certificate'
)
BEGIN
    CREATE CERTIFICATE ag_fixture_certificate
        WITH SUBJECT = 'ag_fixture database mirroring endpoint';
END;
GO

/*
Export for transport to ag2. setup.ps1 deletes any prior copy first, because
BACKUP CERTIFICATE fails outright if the target files already exist.
*/
BACKUP CERTIFICATE ag_fixture_certificate
    TO FILE = '/var/opt/mssql/data/ag_fixture_certificate.cer'
    WITH PRIVATE KEY
    (
        FILE = '/var/opt/mssql/data/ag_fixture_certificate.pvk',
        ENCRYPTION BY PASSWORD = '$(cert_password)'
    );
GO

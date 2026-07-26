/*
Runs on: ag2 (secondary)

Creates ag2's own database master key, then imports the certificate exported from
ag1. AUTHORIZATION dbo matches the owner on ag1 so both endpoints present the same
principal.

setup.ps1 has already docker-cp'd the .cer/.pvk into /var/opt/mssql/data and chowned
them to the mssql user - files landed by docker cp are root-owned by default and
CREATE CERTIFICATE cannot read them.

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
        AUTHORIZATION dbo
        FROM FILE = '/var/opt/mssql/data/ag_fixture_certificate.cer'
        WITH PRIVATE KEY
        (
            FILE = '/var/opt/mssql/data/ag_fixture_certificate.pvk',
            DECRYPTION BY PASSWORD = '$(cert_password)'
        );
END;
GO

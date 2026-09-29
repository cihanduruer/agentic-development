:setvar ApiPrincipalName ""
:setvar ApiPrincipalObjectId ""

DECLARE @ApiPrincipalSid binary(16) =
    CONVERT(binary(16), CONVERT(uniqueidentifier, N'$(ApiPrincipalObjectId)'));

IF EXISTS (
    SELECT 1
    FROM sys.database_principals
    WHERE name = N'$(ApiPrincipalName)'
      AND sid <> @ApiPrincipalSid
)
BEGIN
    IF IS_ROLEMEMBER(N'hotel_booking_runtime', N'$(ApiPrincipalName)') = 1
    BEGIN
        EXEC(N'ALTER ROLE [hotel_booking_runtime] DROP MEMBER ' + QUOTENAME(N'$(ApiPrincipalName)'));
    END;
    EXEC(N'DROP USER ' + QUOTENAME(N'$(ApiPrincipalName)'));
END;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(ApiPrincipalName)')
BEGIN
    DECLARE @ApiPrincipalSidHex varchar(34) = sys.fn_varbintohexstr(@ApiPrincipalSid);
    EXEC(N'CREATE USER ' + QUOTENAME(N'$(ApiPrincipalName)') +
         N' WITH SID = ' + @ApiPrincipalSidHex + N', TYPE = E');
END;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'hotel_booking_runtime')
BEGIN
    CREATE ROLE [hotel_booking_runtime];
END;

GRANT SELECT ON OBJECT::dbo.Hotels TO [hotel_booking_runtime];
GRANT SELECT ON OBJECT::dbo.Rooms TO [hotel_booking_runtime];
GRANT SELECT, INSERT ON OBJECT::dbo.Reservations TO [hotel_booking_runtime];
GRANT SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents TO [hotel_booking_runtime];

IF IS_ROLEMEMBER(N'hotel_booking_runtime', N'$(ApiPrincipalName)') <> 1
BEGIN
    EXEC(N'ALTER ROLE [hotel_booking_runtime] ADD MEMBER ' + QUOTENAME(N'$(ApiPrincipalName)'));
END;

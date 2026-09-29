using System.Data;
using Microsoft.Data.SqlClient;

namespace AgenticHotelBooking.SqlManagedIdentityBootstrapper;

public sealed record SqlBootstrapOptions(
    string Server,
    string Database,
    string PrincipalName,
    Guid PrincipalObjectId,
    string AccessToken)
{
    public static SqlBootstrapOptions Parse(
        string[] args,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Every bootstrap option must have a value.", nameof(args));
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException($"Bootstrap option '{args[index]}' was specified more than once.", nameof(args));
            }
        }

        var supportedOptions = new[]
        {
            "--server",
            "--database",
            "--principal-name",
            "--principal-object-id",
        };
        var unsupportedOption = values.Keys.FirstOrDefault(
            key => !supportedOptions.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unsupportedOption is not null)
        {
            throw new ArgumentException($"Unsupported bootstrap option '{unsupportedOption}'.", nameof(args));
        }

        var server = RequireValue(values, "--server");
        var database = RequireValue(values, "--database");
        var principalName = RequireValue(values, "--principal-name");
        if (principalName.Length > 128)
        {
            throw new ArgumentException("--principal-name cannot exceed 128 characters.", nameof(args));
        }

        var principalObjectIdValue = RequireValue(values, "--principal-object-id");
        if (!Guid.TryParse(principalObjectIdValue, out var principalObjectId))
        {
            throw new ArgumentException("--principal-object-id must be a valid GUID.", nameof(args));
        }

        var accessToken = getEnvironmentVariable("AZURE_SQL_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "AZURE_SQL_ACCESS_TOKEN must contain an Azure SQL access token.");
        }

        return new SqlBootstrapOptions(
            server,
            database,
            principalName,
            principalObjectId,
            accessToken);
    }

    private static string RequireValue(
        Dictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }

        return value;
    }
}

public static class SqlManagedIdentityBootstrap
{
    public const string CommandText = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;

        BEGIN TRY
            BEGIN TRANSACTION;

            DECLARE @ApiPrincipalSid binary(16) =
                CONVERT(binary(16), @apiPrincipalObjectId);
            DECLARE @Command nvarchar(max);

            IF EXISTS (
                SELECT 1
                FROM sys.database_principals
                WHERE name = @apiPrincipalName
                  AND sid <> @ApiPrincipalSid
            )
            BEGIN
                IF IS_ROLEMEMBER(N'hotel_booking_runtime', @apiPrincipalName) = 1
                BEGIN
                    SET @Command =
                        N'ALTER ROLE [hotel_booking_runtime] DROP MEMBER ' +
                        QUOTENAME(@apiPrincipalName) + N';';
                    EXEC sys.sp_executesql @Command;
                END;

                SET @Command = N'DROP USER ' + QUOTENAME(@apiPrincipalName) + N';';
                EXEC sys.sp_executesql @Command;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_principals
                WHERE name = @apiPrincipalName
            )
            BEGIN
                DECLARE @ApiPrincipalSidHex varchar(34) =
                    sys.fn_varbintohexstr(@ApiPrincipalSid);
                SET @Command =
                    N'CREATE USER ' + QUOTENAME(@apiPrincipalName) +
                    N' WITH SID = ' + @ApiPrincipalSidHex + N', TYPE = E;';
                EXEC sys.sp_executesql @Command;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_principals
                WHERE name = N'hotel_booking_runtime'
            )
            BEGIN
                CREATE ROLE [hotel_booking_runtime];
            END;

            GRANT SELECT ON OBJECT::dbo.Hotels TO [hotel_booking_runtime];
            GRANT SELECT ON OBJECT::dbo.Rooms TO [hotel_booking_runtime];
            GRANT SELECT, INSERT ON OBJECT::dbo.Reservations TO [hotel_booking_runtime];
            GRANT SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents TO [hotel_booking_runtime];

            IF COALESCE(IS_ROLEMEMBER(N'hotel_booking_runtime', @apiPrincipalName), 0) <> 1
            BEGIN
                SET @Command =
                    N'ALTER ROLE [hotel_booking_runtime] ADD MEMBER ' +
                    QUOTENAME(@apiPrincipalName) + N';';
                EXEC sys.sp_executesql @Command;
            END;

            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0
            BEGIN
                ROLLBACK TRANSACTION;
            END;
            THROW;
        END CATCH;
        """;

    public static SqlConnection CreateConnection(SqlBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = options.Database,
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = 30,
        }.ConnectionString;

        return new SqlConnection(connectionString)
        {
            AccessToken = options.AccessToken,
        };
    }

    public static SqlCommand CreateCommand(
        SqlConnection connection,
        SqlBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        var command = connection.CreateCommand();
        command.CommandText = CommandText;
        command.CommandTimeout = 60;
        command.Parameters.Add(
            new SqlParameter("@apiPrincipalName", SqlDbType.NVarChar, 128)
            {
                Value = options.PrincipalName,
            });
        command.Parameters.Add(
            new SqlParameter("@apiPrincipalObjectId", SqlDbType.UniqueIdentifier)
            {
                Value = options.PrincipalObjectId,
            });
        return command;
    }
}

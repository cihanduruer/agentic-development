using System.Data;
using Microsoft.Data.SqlClient;

namespace AgenticHotelBooking.SqlManagedIdentityBootstrapper;

public sealed record SqlBootstrapOptions(
    string Server,
    string Database,
    string PrincipalName,
    Guid PrincipalObjectId,
    string AccessToken,
    SqlBootstrapMode Mode = SqlBootstrapMode.Bootstrap,
    string? DiagnosticOutputPath = null)
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
            "--mode",
            "--output",
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

        var modeValue = values.GetValueOrDefault("--mode") ?? "bootstrap";
        if (!Enum.TryParse<SqlBootstrapMode>(modeValue, true, out var mode))
        {
            throw new ArgumentException(
                "--mode must be either bootstrap or diagnostic.",
                nameof(args));
        }

        values.TryGetValue("--output", out var outputPath);
        if (mode == SqlBootstrapMode.Diagnostic && string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException(
                "--output is required in diagnostic mode.",
                nameof(args));
        }

        if (mode == SqlBootstrapMode.Bootstrap && outputPath is not null)
        {
            throw new ArgumentException(
                "--output is supported only in diagnostic mode.",
                nameof(args));
        }

        return new SqlBootstrapOptions(
            server,
            database,
            principalName,
            principalObjectId,
            accessToken,
            mode,
            outputPath);
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

public enum SqlBootstrapMode
{
    Bootstrap,
    Diagnostic,
}

public static class SqlManagedIdentityBootstrap
{
    internal static IReadOnlyList<SqlObjectGrant> RecoverableDirectPermissions { get; } =
        Array.AsReadOnly<SqlObjectGrant>(
        [
            new("dbo.Hotels", "SELECT"),
            new("dbo.Rooms", "SELECT"),
            new("dbo.Reservations", "SELECT"),
            new("dbo.Reservations", "INSERT"),
            new("dbo.AgentEvents", "SELECT"),
            new("dbo.AgentEvents", "INSERT"),
            new("dbo.AgentEvents", "DELETE"),
        ]);

    internal static bool IsExactRecoverableDirectPermissionSet(
        IEnumerable<SqlDatabaseGrant> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var actual = permissions.ToArray();
        if (actual.Length != RecoverableDirectPermissions.Count
            || actual.Any(
                permission => permission.Class != 1
                    || permission.MinorId != 0
                    || permission.State != "G"))
        {
            return false;
        }

        return actual
            .Select(permission => new SqlObjectGrant(
                permission.ObjectName,
                permission.PermissionName))
            .ToHashSet()
            .SetEquals(RecoverableDirectPermissions);
    }

    internal static bool HasNoExplicitPermissionsOnRuntimeRole(
        IEnumerable<SqlDatabasePermissionEntry> permissions,
        int runtimeRolePrincipalId)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        return !permissions.Any(
            permission => permission.Class == 4
                && permission.MajorId == runtimeRolePrincipalId);
    }

    public const string CommandText = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;

        BEGIN TRY
            BEGIN TRANSACTION;

            DECLARE @ApiPrincipalSid binary(16) =
                CONVERT(binary(16), @apiPrincipalObjectId);
            DECLARE @Command nvarchar(max);
            DECLARE @ExistingApiPrincipalId int =
                DATABASE_PRINCIPAL_ID(@apiPrincipalName);

            IF (
                SELECT COUNT_BIG(*)
                FROM sys.database_principals
                WHERE name = @apiPrincipalName
                   OR sid = @ApiPrincipalSid
            ) > 1
               OR (
                   @ExistingApiPrincipalId IS NOT NULL
                   AND NOT EXISTS (
                       SELECT 1
                       FROM sys.database_principals
                       WHERE principal_id = @ExistingApiPrincipalId
                         AND name = @apiPrincipalName
                         AND sid = @ApiPrincipalSid
                         AND type = @apiPrincipalType
                         AND authentication_type_desc = @apiAuthenticationType
                   )
               )
               OR (
                   @ExistingApiPrincipalId IS NULL
                   AND EXISTS (
                       SELECT 1
                       FROM sys.database_principals
                       WHERE sid = @ApiPrincipalSid
                   )
               )
            BEGIN
                DECLARE @ActualApiAuthenticationType nvarchar(60);
                IF (
                    SELECT COUNT_BIG(*)
                    FROM sys.database_principals
                    WHERE name = @apiPrincipalName
                       OR sid = @ApiPrincipalSid
                ) > 1
                BEGIN
                    SET @ActualApiAuthenticationType = N'<ambiguous>';
                END
                ELSE
                BEGIN
                    SELECT @ActualApiAuthenticationType =
                        authentication_type_desc
                    FROM sys.database_principals
                    WHERE name = @apiPrincipalName
                       OR sid = @ApiPrincipalSid;
                END;
                DECLARE @IdentityMismatchMessage nvarchar(2048) = CONCAT(
                    N'The API principal name, SID, type, or authentication type does not match the expected identity. ',
                    N'Expected authentication type: ',
                    @apiAuthenticationType,
                    N'; actual authentication type: ',
                    COALESCE(@ActualApiAuthenticationType, N'<missing>'),
                    N'.');
                THROW 51007, @IdentityMismatchMessage, 1;
            END;

            IF @ExistingApiPrincipalId IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM sys.database_role_members AS memberships
                   INNER JOIN sys.database_principals AS roles
                       ON roles.principal_id = memberships.role_principal_id
                   WHERE memberships.member_principal_id = @ExistingApiPrincipalId
                     AND roles.name <> N'hotel_booking_runtime'
               )
            BEGIN
                THROW 51001, 'The API principal has unexpected database role memberships.', 1;
            END;

            IF @ExistingApiPrincipalId IS NOT NULL
               AND (
                   EXISTS (
                       SELECT 1
                       FROM sys.schemas
                       WHERE principal_id = @ExistingApiPrincipalId
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM sys.objects
                       WHERE principal_id = @ExistingApiPrincipalId
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM sys.database_principals
                       WHERE owning_principal_id = @ExistingApiPrincipalId
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM sys.databases
                       WHERE database_id = DB_ID()
                         AND owner_sid = (
                             SELECT sid
                             FROM sys.database_principals
                             WHERE principal_id = @ExistingApiPrincipalId
                         )
                   )
               )
            BEGIN
                THROW 51002, 'The API principal unexpectedly owns database securables.', 1;
            END;

            DECLARE @ExistingRuntimeRoleId int =
                DATABASE_PRINCIPAL_ID(N'hotel_booking_runtime');
            DECLARE @DboPrincipalId int =
                DATABASE_PRINCIPAL_ID(N'dbo');
            IF @DboPrincipalId IS NULL
            BEGIN
                THROW 51014, 'The canonical dbo database principal does not exist.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND NOT EXISTS (
                   SELECT 1
                   FROM sys.database_principals
                   WHERE principal_id = @ExistingRuntimeRoleId
                     AND type = N'R'
               )
            BEGIN
                THROW 51011, 'The runtime role name belongs to an unexpected database principal.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM sys.database_principals
                   WHERE principal_id = @ExistingRuntimeRoleId
                     AND (
                         owning_principal_id IS NULL
                         OR owning_principal_id <> @DboPrincipalId
                     )
               )
            BEGIN
                THROW 51015, 'The runtime role has an unexpected owner.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND (
                   EXISTS (
                       SELECT 1
                       FROM sys.schemas
                       WHERE principal_id = @ExistingRuntimeRoleId
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM sys.objects
                       WHERE principal_id = @ExistingRuntimeRoleId
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM sys.database_principals
                       WHERE owning_principal_id = @ExistingRuntimeRoleId
                   )
                   OR EXISTS (
                       SELECT 1
                       FROM sys.databases
                       WHERE database_id = DB_ID()
                         AND owner_sid = (
                             SELECT sid
                             FROM sys.database_principals
                             WHERE principal_id = @ExistingRuntimeRoleId
                         )
                   )
               )
            BEGIN
                THROW 51003, 'The runtime role unexpectedly owns database securables.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM sys.database_role_members
                   WHERE member_principal_id = @ExistingRuntimeRoleId
               )
            BEGIN
                THROW 51004, 'The runtime role is unexpectedly nested in another database role.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM sys.database_role_members
                   WHERE role_principal_id = @ExistingRuntimeRoleId
                     AND (
                         @ExistingApiPrincipalId IS NULL
                         OR member_principal_id <> @ExistingApiPrincipalId
                     )
               )
            BEGIN
                THROW 51012, 'The runtime role has unexpected database principals as members.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM sys.database_permissions AS permissions
                   WHERE permissions.class = 4
                     AND permissions.major_id = @ExistingRuntimeRoleId
               )
            BEGIN
                THROW 51017, 'The runtime role has delegated database-principal permissions.', 1;
            END;

            IF @ExistingRuntimeRoleId IS NOT NULL
               AND EXISTS (
                   SELECT 1
                   FROM sys.database_permissions AS permissions
                   WHERE permissions.grantee_principal_id = @ExistingRuntimeRoleId
                     AND NOT (
                         permissions.class = 1
                         AND permissions.minor_id = 0
                         AND permissions.state = N'G'
                         AND (
                             (permissions.major_id = OBJECT_ID(N'dbo.Hotels')
                                 AND permissions.permission_name = N'SELECT')
                             OR (permissions.major_id = OBJECT_ID(N'dbo.Rooms')
                                 AND permissions.permission_name = N'SELECT')
                             OR (permissions.major_id = OBJECT_ID(N'dbo.Reservations')
                                 AND permissions.permission_name IN (N'SELECT', N'INSERT'))
                             OR (permissions.major_id = OBJECT_ID(N'dbo.AgentEvents')
                                 AND permissions.permission_name IN (N'SELECT', N'INSERT', N'DELETE'))
                         )
                     )
               )
            BEGIN
                THROW 51005, 'The runtime role has unexpected database permissions.', 1;
            END;

            IF OBJECT_ID(N'dbo.Hotels', N'U') IS NULL
               OR OBJECT_ID(N'dbo.Rooms', N'U') IS NULL
               OR OBJECT_ID(N'dbo.Reservations', N'U') IS NULL
               OR OBJECT_ID(N'dbo.AgentEvents', N'U') IS NULL
            BEGIN
                THROW 51008, 'The expected runtime database objects do not all exist.', 1;
            END;

            DECLARE @ExpectedRuntimePermissions TABLE (
                class tinyint NOT NULL,
                major_id int NOT NULL,
                minor_id int NOT NULL,
                permission_name nvarchar(128) NOT NULL,
                state char(1) NOT NULL,
                PRIMARY KEY (class, major_id, minor_id, permission_name, state)
            );
            INSERT INTO @ExpectedRuntimePermissions (
                class,
                major_id,
                minor_id,
                permission_name,
                state
            )
            VALUES
                (1, OBJECT_ID(N'dbo.Hotels'), 0, N'SELECT', N'G'),
                (1, OBJECT_ID(N'dbo.Rooms'), 0, N'SELECT', N'G'),
                (1, OBJECT_ID(N'dbo.Reservations'), 0, N'SELECT', N'G'),
                (1, OBJECT_ID(N'dbo.Reservations'), 0, N'INSERT', N'G'),
                (1, OBJECT_ID(N'dbo.AgentEvents'), 0, N'SELECT', N'G'),
                (1, OBJECT_ID(N'dbo.AgentEvents'), 0, N'INSERT', N'G'),
                (1, OBJECT_ID(N'dbo.AgentEvents'), 0, N'DELETE', N'G');

            DECLARE @ExistingDirectPermissionCount bigint = (
                SELECT COUNT_BIG(*)
                FROM sys.database_permissions
                WHERE grantee_principal_id = @ExistingApiPrincipalId
            );
            IF @ExistingDirectPermissionCount > 0
            BEGIN
                IF @ExistingDirectPermissionCount <> 7
                   OR EXISTS (
                       SELECT
                           permissions.class,
                           permissions.major_id,
                           permissions.minor_id,
                           permissions.permission_name COLLATE DATABASE_DEFAULT,
                           permissions.state COLLATE DATABASE_DEFAULT
                       FROM sys.database_permissions AS permissions
                       WHERE permissions.grantee_principal_id = @ExistingApiPrincipalId
                       EXCEPT
                       SELECT
                           class,
                           major_id,
                           minor_id,
                           permission_name,
                           state
                       FROM @ExpectedRuntimePermissions
                   )
                   OR EXISTS (
                       SELECT
                           class,
                           major_id,
                           minor_id,
                           permission_name,
                           state
                       FROM @ExpectedRuntimePermissions
                       EXCEPT
                       SELECT
                           permissions.class,
                           permissions.major_id,
                           permissions.minor_id,
                           permissions.permission_name COLLATE DATABASE_DEFAULT,
                           permissions.state COLLATE DATABASE_DEFAULT
                       FROM sys.database_permissions AS permissions
                       WHERE permissions.grantee_principal_id = @ExistingApiPrincipalId
                   )
                BEGIN
                    THROW 51000, 'The API principal has unexpected direct database permissions.', 1;
                END;

                SET @Command =
                    N'REVOKE SELECT ON OBJECT::dbo.Hotels FROM ' +
                    QUOTENAME(@apiPrincipalName) + N';' +
                    N'REVOKE SELECT ON OBJECT::dbo.Rooms FROM ' +
                    QUOTENAME(@apiPrincipalName) + N';' +
                    N'REVOKE SELECT, INSERT ON OBJECT::dbo.Reservations FROM ' +
                    QUOTENAME(@apiPrincipalName) + N';' +
                    N'REVOKE SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents FROM ' +
                    QUOTENAME(@apiPrincipalName) + N';';
                EXEC sys.sp_executesql @Command;

                IF EXISTS (
                    SELECT 1
                    FROM sys.database_permissions
                    WHERE grantee_principal_id = @ExistingApiPrincipalId
                )
                BEGIN
                    THROW 51009, 'The API principal still has direct database permissions after legacy migration.', 1;
                END;
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

            SET @ExistingApiPrincipalId =
                DATABASE_PRINCIPAL_ID(@apiPrincipalName);

            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_principals
                WHERE name = N'hotel_booking_runtime'
            )
            BEGIN
                CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [dbo];
            END;

            GRANT SELECT ON OBJECT::dbo.Hotels TO [hotel_booking_runtime];
            GRANT SELECT ON OBJECT::dbo.Rooms TO [hotel_booking_runtime];
            GRANT SELECT, INSERT ON OBJECT::dbo.Reservations TO [hotel_booking_runtime];
            GRANT SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents TO [hotel_booking_runtime];

            DECLARE @RuntimeRoleId int =
                DATABASE_PRINCIPAL_ID(N'hotel_booking_runtime');
            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_principals
                WHERE principal_id = @RuntimeRoleId
                  AND type = N'R'
                  AND owning_principal_id = @DboPrincipalId
            )
            BEGIN
                THROW 51016, 'The runtime role does not have the canonical owner.', 1;
            END;

            IF (
                SELECT COUNT_BIG(*)
                FROM sys.database_permissions AS permissions
                WHERE permissions.grantee_principal_id = @RuntimeRoleId
            ) <> 7
               OR EXISTS (
                   SELECT
                       permissions.class,
                       permissions.major_id,
                       permissions.minor_id,
                       permissions.permission_name COLLATE DATABASE_DEFAULT,
                       permissions.state COLLATE DATABASE_DEFAULT
                   FROM sys.database_permissions AS permissions
                   WHERE permissions.grantee_principal_id = @RuntimeRoleId
                   EXCEPT
                   SELECT
                       class,
                       major_id,
                       minor_id,
                       permission_name,
                       state
                   FROM @ExpectedRuntimePermissions
               )
               OR EXISTS (
                   SELECT
                       class,
                       major_id,
                       minor_id,
                       permission_name,
                       state
                   FROM @ExpectedRuntimePermissions
                   EXCEPT
                   SELECT
                       permissions.class,
                       permissions.major_id,
                       permissions.minor_id,
                       permissions.permission_name COLLATE DATABASE_DEFAULT,
                       permissions.state COLLATE DATABASE_DEFAULT
                   FROM sys.database_permissions AS permissions
                   WHERE permissions.grantee_principal_id = @RuntimeRoleId
               )
            BEGIN
                THROW 51006, 'The runtime role does not have the exact expected permission set.', 1;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.database_role_members
                WHERE role_principal_id = @RuntimeRoleId
                  AND member_principal_id = @ExistingApiPrincipalId
            )
            BEGIN
                SET @Command =
                    N'ALTER ROLE [hotel_booking_runtime] ADD MEMBER ' +
                    QUOTENAME(@apiPrincipalName) + N';';
                EXEC sys.sp_executesql @Command;
            END;

            IF (
                SELECT COUNT_BIG(*)
                FROM sys.database_role_members
                WHERE role_principal_id = @RuntimeRoleId
            ) <> 1
               OR NOT EXISTS (
                   SELECT 1
                   FROM sys.database_role_members
                   WHERE role_principal_id = @RuntimeRoleId
                     AND member_principal_id = @ExistingApiPrincipalId
               )
            BEGIN
                THROW 51013, 'The runtime role does not have the exact expected membership.', 1;
            END;

            IF EXISTS (
                SELECT 1
                FROM sys.database_permissions AS permissions
                WHERE permissions.class = 4
                  AND permissions.major_id = @RuntimeRoleId
            )
            BEGIN
                THROW 51018, 'The runtime role has delegated database-principal permissions after bootstrap.', 1;
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

    public const string DiagnosticCommandText = """
        WITH identity_candidates AS (
            SELECT
                principals.principal_id,
                principals.name,
                principals.sid,
                principals.type,
                principals.type_desc,
                principals.authentication_type_desc,
                principals.owning_principal_id
            FROM sys.database_principals AS principals
            WHERE principals.name = @apiPrincipalName
               OR principals.sid = CONVERT(binary(16), @apiPrincipalObjectId)
        ),
        relevant_principals AS (
            SELECT principal_id
            FROM identity_candidates
            UNION
            SELECT principal_id
            FROM sys.database_principals
            WHERE name = N'hotel_booking_runtime'
        )
        SELECT
            @apiPrincipalName AS [target.principalName],
            CONVERT(nvarchar(36), @apiPrincipalObjectId) AS [target.principalObjectId],
            DB_NAME() AS [databaseName],
            SUSER_SNAME() AS [observer.name],
            IS_ROLEMEMBER(N'db_owner') AS [observer.isDatabaseOwner],
            HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION')
                AS [observer.canViewDefinition],
            @@TRANCOUNT AS [transactionCount],
            JSON_QUERY(COALESCE((
                SELECT
                    candidate.principal_id AS principalId,
                    candidate.name,
                    sys.fn_varbintohexstr(candidate.sid) AS sid,
                    candidate.type,
                    candidate.type_desc AS typeDescription,
                    candidate.authentication_type_desc AS authenticationType,
                    owner.name AS ownerName
                FROM identity_candidates AS candidate
                LEFT JOIN sys.database_principals AS owner
                    ON owner.principal_id = candidate.owning_principal_id
                ORDER BY candidate.principal_id
                FOR JSON PATH
            ), N'[]')) AS identityCandidates,
            JSON_QUERY(COALESCE((
                SELECT
                    grantee.name AS grantee,
                    permissions.state,
                    permissions.state_desc AS stateDescription,
                    permissions.class,
                    permissions.class_desc AS classDescription,
                    schema_value.name AS schemaName,
                    object_value.name AS objectName,
                    column_value.name AS columnName,
                    permissions.permission_name AS permission,
                    grantor.name AS grantor
                FROM sys.database_permissions AS permissions
                INNER JOIN relevant_principals AS relevant
                    ON relevant.principal_id = permissions.grantee_principal_id
                INNER JOIN sys.database_principals AS grantee
                    ON grantee.principal_id = permissions.grantee_principal_id
                LEFT JOIN sys.objects AS object_value
                    ON permissions.class = 1
                   AND object_value.object_id = permissions.major_id
                LEFT JOIN sys.schemas AS schema_value
                    ON (permissions.class = 1
                        AND schema_value.schema_id = object_value.schema_id)
                    OR (permissions.class = 3
                        AND schema_value.schema_id = permissions.major_id)
                LEFT JOIN sys.columns AS column_value
                    ON permissions.class = 1
                   AND column_value.object_id = permissions.major_id
                   AND column_value.column_id = permissions.minor_id
                LEFT JOIN sys.database_principals AS grantor
                    ON grantor.principal_id = permissions.grantor_principal_id
                ORDER BY
                    grantee.name,
                    permissions.class,
                    schema_value.name,
                    object_value.name,
                    column_value.name,
                    permissions.permission_name,
                    permissions.state
                FOR JSON PATH
            ), N'[]')) AS directPermissions,
            JSON_QUERY(COALESCE((
                SELECT
                    role_value.name AS roleName,
                    member_value.name AS memberName
                FROM sys.database_role_members AS memberships
                INNER JOIN sys.database_principals AS role_value
                    ON role_value.principal_id = memberships.role_principal_id
                INNER JOIN sys.database_principals AS member_value
                    ON member_value.principal_id = memberships.member_principal_id
                ORDER BY role_value.name, member_value.name
                FOR JSON PATH
            ), N'[]')) AS roleMemberships,
            JSON_QUERY(COALESCE((
                SELECT
                    role_value.name AS roleName,
                    owner.name AS ownerName,
                    role_value.owning_principal_id AS ownerPrincipalId
                FROM sys.database_principals AS role_value
                LEFT JOIN sys.database_principals AS owner
                    ON owner.principal_id = role_value.owning_principal_id
                WHERE role_value.type = N'R'
                  AND role_value.principal_id IN (
                      SELECT principal_id FROM relevant_principals
                  )
                ORDER BY role_value.name
                FOR JSON PATH
            ), N'[]')) AS roleOwnership,
            JSON_QUERY(COALESCE((
                SELECT
                    grantee.name AS grantee,
                    target.name AS targetPrincipal,
                    permissions.state,
                    permissions.permission_name AS permission,
                    grantor.name AS grantor
                FROM sys.database_permissions AS permissions
                INNER JOIN sys.database_principals AS target
                    ON permissions.class = 4
                   AND target.principal_id = permissions.major_id
                INNER JOIN sys.database_principals AS grantee
                    ON grantee.principal_id = permissions.grantee_principal_id
                LEFT JOIN sys.database_principals AS grantor
                    ON grantor.principal_id = permissions.grantor_principal_id
                WHERE permissions.major_id IN (
                    SELECT principal_id FROM relevant_principals
                )
                ORDER BY
                    target.name,
                    grantee.name,
                    permissions.permission_name,
                    permissions.state
                FOR JSON PATH
            ), N'[]')) AS delegatedPermissions,
            JSON_QUERY(COALESCE((
                SELECT
                    ownership.securableType,
                    ownership.schemaName,
                    ownership.securableName,
                    ownership.ownerName
                FROM (
                    SELECT
                        N'SCHEMA' AS securableType,
                        schemas.name AS schemaName,
                        schemas.name AS securableName,
                        owner.name AS ownerName
                    FROM sys.schemas AS schemas
                    INNER JOIN relevant_principals AS relevant
                        ON relevant.principal_id = schemas.principal_id
                    INNER JOIN sys.database_principals AS owner
                        ON owner.principal_id = schemas.principal_id
                    UNION ALL
                    SELECT
                        N'OBJECT',
                        schemas.name,
                        objects.name,
                        owner.name
                    FROM sys.objects AS objects
                    INNER JOIN sys.schemas AS schemas
                        ON schemas.schema_id = objects.schema_id
                    INNER JOIN relevant_principals AS relevant
                        ON relevant.principal_id = objects.principal_id
                    INNER JOIN sys.database_principals AS owner
                        ON owner.principal_id = objects.principal_id
                    UNION ALL
                    SELECT
                        N'DATABASE_PRINCIPAL',
                        NULL,
                        owned.name,
                        owner.name
                    FROM sys.database_principals AS owned
                    INNER JOIN relevant_principals AS relevant
                        ON relevant.principal_id = owned.owning_principal_id
                    INNER JOIN sys.database_principals AS owner
                        ON owner.principal_id = owned.owning_principal_id
                ) AS ownership
                ORDER BY
                    ownership.securableType,
                    ownership.schemaName,
                    ownership.securableName
                FOR JSON PATH
            ), N'[]')) AS ownedSecurables
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
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
            ApplicationIntent = options.Mode == SqlBootstrapMode.Diagnostic
                ? ApplicationIntent.ReadOnly
                : ApplicationIntent.ReadWrite,
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
        command.Parameters.Add(
            new SqlParameter("@apiPrincipalType", SqlDbType.Char, 1)
            {
                Value = "E",
            });
        command.Parameters.Add(
            new SqlParameter("@apiAuthenticationType", SqlDbType.NVarChar, 60)
            {
                Value = "EXTERNAL",
            });
        return command;
    }

    public static SqlCommand CreateDiagnosticCommand(
        SqlConnection connection,
        SqlBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Mode != SqlBootstrapMode.Diagnostic)
        {
            throw new ArgumentException(
                "Diagnostic command creation requires diagnostic mode.",
                nameof(options));
        }

        var command = connection.CreateCommand();
        command.CommandText = DiagnosticCommandText;
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

internal sealed record SqlObjectGrant(
    string ObjectName,
    string PermissionName);

internal sealed record SqlDatabaseGrant(
    string ObjectName,
    string PermissionName,
    int Class = 1,
    int MinorId = 0,
    string State = "G");

internal sealed record SqlDatabasePermissionEntry(
    int Class,
    int MajorId,
    int MinorId,
    string PermissionName,
    string State,
    int GranteePrincipalId);

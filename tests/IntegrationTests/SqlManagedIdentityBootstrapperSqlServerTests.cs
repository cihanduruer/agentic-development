using System.Data;
using AgenticHotelBooking.SqlManagedIdentityBootstrapper;
using Microsoft.Data.SqlClient;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class SqlManagedIdentityBootstrapperSqlServerTests
{
    private const string PrincipalName = "agentic-api";

    [SqlServerFact]
    public async Task ExactDirectPermissionContractMigratesAndRerunsIdempotently()
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);

                await ExecuteBootstrap(connection, principalObjectId);

                Assert.Equal(0, await CountDirectPermissions(connection));
                await AssertRuntimeRoleContract(connection);

                await ExecuteBootstrap(connection, principalObjectId);

                Assert.Equal(0, await CountDirectPermissions(connection));
                await AssertRuntimeRoleContract(connection);
            });
    }

    [SqlServerTheory]
    [InlineData("subset")]
    [InlineData("superset")]
    [InlineData("deny")]
    [InlineData("grant-option")]
    [InlineData("column")]
    public async Task MutatedDirectPermissionStateFailsClosed(string mutation)
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, DirectGrantsFor(mutation));
                var before = await ReadDirectPermissions(connection);

                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrap(connection, principalObjectId));

                Assert.Equal(51000, exception.Number);
                Assert.Equal(before, await ReadDirectPermissions(connection));
                Assert.Equal(0, await CountRuntimeRoles(connection));
            });
    }

    [SqlServerTheory]
    [InlineData("user")]
    [InlineData("role")]
    public async Task UnexpectedRuntimeRoleOwnerFailsClosed(string ownerType)
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);
                await CreateAttackerOwnedRuntimeRole(connection, ownerType);
                var before = await ReadDirectPermissions(connection);

                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrap(connection, principalObjectId));

                Assert.Equal(51015, exception.Number);
                Assert.Equal(before, await ReadDirectPermissions(connection));
                Assert.Equal(
                    ownerType == "user" ? "attacker" : "attacker_owner",
                    await ReadRuntimeRoleOwner(connection));
                Assert.Equal(0, await CountRuntimeRolePermissions(connection));
            });
    }

    [SqlServerFact]
    public async Task UnexpectedRuntimeRoleMemberFailsClosed()
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);
                await ExecuteNonQuery(
                    connection,
                    """
                    CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [dbo];
                    CREATE USER [attacker] WITHOUT LOGIN;
                    ALTER ROLE [hotel_booking_runtime] ADD MEMBER [attacker];
                    """);
                var before = await ReadDirectPermissions(connection);

                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrap(connection, principalObjectId));

                Assert.Equal(51012, exception.Number);
                Assert.Equal(before, await ReadDirectPermissions(connection));
                Assert.Equal(0, await CountRuntimeRolePermissions(connection));
                Assert.Equal(1, await CountRuntimeRoleMembers(connection));
            });
    }

    [SqlServerFact]
    public async Task IndirectApiMembershipDoesNotSatisfyDirectMembershipContract()
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);
                await ExecuteNonQuery(
                    connection,
                    """
                    CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [dbo];
                    CREATE ROLE [intermediary] AUTHORIZATION [dbo];
                    ALTER ROLE [intermediary] ADD MEMBER [agentic-api];
                    ALTER ROLE [hotel_booking_runtime] ADD MEMBER [intermediary];
                    """);
                var before = await ReadDirectPermissions(connection);

                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrap(connection, principalObjectId));

                Assert.Equal(51001, exception.Number);
                Assert.Equal(before, await ReadDirectPermissions(connection));
                Assert.Equal(0, await CountRuntimeRolePermissions(connection));
                Assert.Equal(1, await CountRuntimeRoleMembers(connection));
            });
    }

    [SqlServerTheory]
    [InlineData("name")]
    [InlineData("sid")]
    [InlineData("type")]
    [InlineData("authentication")]
    public async Task ExistingApiPrincipalIdentityMismatchFailsClosed(
        string mutation)
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);
                var before = await ReadDirectPermissions(connection);

                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrapCommand(
                        connection,
                        mutation == "sid" ? Guid.NewGuid() : principalObjectId,
                        SqlManagedIdentityBootstrap.CommandText,
                        mutation == "name" ? "unexpected-api" : PrincipalName,
                        mutation == "type" ? "E" : "S",
                        mutation == "authentication" ? "EXTERNAL" : "INSTANCE"));

                Assert.Equal(51007, exception.Number);
                if (mutation == "authentication")
                {
                    Assert.Contains(
                        "Expected authentication type: EXTERNAL; " +
                        "actual authentication type: INSTANCE.",
                        exception.Message,
                        StringComparison.Ordinal);
                }
                Assert.Equal(before, await ReadDirectPermissions(connection));
                Assert.Equal(0, await CountRuntimeRoles(connection));
            });
    }

    [SqlServerTheory]
    [MemberData(nameof(DelegatedRuntimeRolePermissionStates))]
    public async Task DelegatedRuntimeRolePermissionFailsClosed(
        string permissionName,
        string state,
        string granteeType)
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);
                await ExecuteNonQuery(
                    connection,
                    DelegatedRuntimeRolePermissionSql(
                        permissionName,
                        state,
                        granteeType));
                var directBefore = await ReadDirectPermissions(connection);
                var delegatedBefore =
                    await ReadDelegatedRuntimeRolePermissions(connection);

                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrap(connection, principalObjectId));

                Assert.Equal(51017, exception.Number);
                Assert.Equal(directBefore, await ReadDirectPermissions(connection));
                Assert.Equal(
                    delegatedBefore,
                    await ReadDelegatedRuntimeRolePermissions(connection));
                Assert.Equal(0, await CountRuntimeRolePermissions(connection));
                Assert.Equal(0, await CountRuntimeRoleMembers(connection));
            });
    }

    [SqlServerFact]
    public async Task FailureImmediatelyBeforeCommitRollsBackEveryMutation()
    {
        await InIsolatedDatabase(
            async (connection, principalObjectId) =>
            {
                await ExecuteNonQuery(connection, ExactDirectGrants);
                await ExecuteNonQuery(
                    connection,
                    "CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [dbo];");
                var directBefore = await ReadDirectPermissions(connection);

                var commandText = SqlManagedIdentityBootstrap.CommandText.Replace(
                    "COMMIT TRANSACTION;",
                    "THROW 51999, 'Injected failure before commit.', 1;",
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "COMMIT TRANSACTION;",
                    commandText,
                    StringComparison.Ordinal);
                var exception = await Assert.ThrowsAsync<SqlException>(
                    () => ExecuteBootstrapCommand(
                        connection,
                        principalObjectId,
                        commandText));

                Assert.Equal(51999, exception.Number);
                Assert.Equal(directBefore, await ReadDirectPermissions(connection));
                Assert.Equal("dbo", await ReadRuntimeRoleOwner(connection));
                Assert.Equal(0, await CountRuntimeRolePermissions(connection));
                Assert.Equal(0, await CountRuntimeRoleMembers(connection));
                Assert.Empty(await ReadDelegatedRuntimeRolePermissions(connection));
            });
    }

    private static async Task InIsolatedDatabase(
        Func<SqlConnection, Guid, Task> test)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            "SQL_BOOTSTRAP_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                "SQL_BOOTSTRAP_TEST_CONNECTION is required for SQL Server bootstrap tests.");
        }

        var databaseName = $"bootstrap_{Guid.NewGuid():N}";
        var loginName = $"bootstrap_login_{Guid.NewGuid():N}";
        var masterConnectionString = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        await using var master = await OpenWithRetry(masterConnectionString);
        await ExecuteNonQuery(master, $"CREATE DATABASE [{databaseName}];");
        var principalObjectId = await CreateSqlLogin(master, loginName);

        try
        {
            var databaseConnectionString =
                new SqlConnectionStringBuilder(baseConnectionString)
                {
                    InitialCatalog = databaseName,
                }.ConnectionString;
            await using var database = new SqlConnection(databaseConnectionString);
            await database.OpenAsync();
            await ExecuteNonQuery(
                database,
                $"""
                CREATE TABLE dbo.Hotels (Id int NOT NULL);
                CREATE TABLE dbo.Rooms (Id int NOT NULL);
                CREATE TABLE dbo.Reservations (Id int NOT NULL);
                CREATE TABLE dbo.AgentEvents (Id int NOT NULL);
                CREATE USER [agentic-api] FOR LOGIN [{loginName}];
                REVOKE CONNECT FROM [agentic-api];
                """);
            await test(database, principalObjectId);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await ExecuteNonQuery(
                master,
                $"""
                ALTER DATABASE [{databaseName}]
                    SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
                """);
            await ExecuteNonQuery(master, $"DROP LOGIN [{loginName}];");
        }
    }

    private static async Task<SqlConnection> OpenWithRetry(
        string connectionString)
    {
        SqlException? lastException = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch (SqlException exception)
            {
                lastException = exception;
                await connection.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException(
            "The SQL Server test service did not become ready.",
            lastException);
    }

    private static async Task<Guid> CreateSqlLogin(
        SqlConnection connection,
        string loginName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            CREATE LOGIN [{loginName}]
                WITH PASSWORD = 'Local-Bootstrap-2026!';
            SELECT CONVERT(uniqueidentifier, sid)
            FROM sys.server_principals
            WHERE name = N'{loginName}';
            """;
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteBootstrap(
        SqlConnection connection,
        Guid principalObjectId) =>
        await ExecuteBootstrapCommand(
            connection,
            principalObjectId,
            SqlManagedIdentityBootstrap.CommandText);

    private static async Task ExecuteBootstrapCommand(
        SqlConnection connection,
        Guid principalObjectId,
        string commandText,
        string principalName = PrincipalName,
        string principalType = "S",
        string authenticationType = "INSTANCE")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.Add(
            new SqlParameter("@apiPrincipalName", SqlDbType.NVarChar, 128)
            {
                Value = principalName,
            });
        command.Parameters.Add(
            new SqlParameter("@apiPrincipalObjectId", SqlDbType.UniqueIdentifier)
            {
                Value = principalObjectId,
            });
        command.Parameters.Add(
            new SqlParameter("@apiPrincipalType", SqlDbType.Char, 1)
            {
                Value = principalType,
            });
        command.Parameters.Add(
            new SqlParameter("@apiAuthenticationType", SqlDbType.NVarChar, 60)
            {
                Value = authenticationType,
            });
        await command.ExecuteNonQueryAsync();
    }

    public static TheoryData<string, string, string>
        DelegatedRuntimeRolePermissionStates()
    {
        var states = new TheoryData<string, string, string>();
        foreach (var permissionName in new[] { "ALTER", "CONTROL", "TAKE OWNERSHIP" })
        {
            foreach (var state in new[] { "G", "W" })
            {
                states.Add(permissionName, state, "user");
                states.Add(permissionName, state, "role");
            }
        }

        return states;
    }

    private static async Task CreateAttackerOwnedRuntimeRole(
        SqlConnection connection,
        string ownerType)
    {
        var commandText = ownerType switch
        {
            "user" =>
                """
                CREATE USER [attacker] WITHOUT LOGIN;
                CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [attacker];
                """,
            "role" =>
                """
                CREATE ROLE [attacker_owner] AUTHORIZATION [dbo];
                CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [attacker_owner];
                """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(ownerType),
                ownerType,
                "Unsupported owner type."),
        };
        await ExecuteNonQuery(connection, commandText);
    }

    private static string DelegatedRuntimeRolePermissionSql(
        string permissionName,
        string state,
        string granteeType)
    {
        var createGrantee = granteeType switch
        {
            "user" => "CREATE USER [attacker] WITHOUT LOGIN;",
            "role" => "CREATE ROLE [attacker] AUTHORIZATION [dbo];",
            _ => throw new ArgumentOutOfRangeException(
                nameof(granteeType),
                granteeType,
                "Unsupported grantee type."),
        };
        var grantOption = state switch
        {
            "G" => string.Empty,
            "W" => " WITH GRANT OPTION",
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Unsupported permission state."),
        };

        return
            $"""
            CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [dbo];
            {createGrantee}
            GRANT {permissionName}
                ON ROLE::[hotel_booking_runtime] TO [attacker]{grantOption};
            """;
    }

    private static async Task AssertRuntimeRoleContract(
        SqlConnection connection)
    {
        Assert.Equal("dbo", await ReadRuntimeRoleOwner(connection));
        Assert.Equal(7, await CountRuntimeRolePermissions(connection));
        Assert.Equal(
            ExpectedRuntimePermissionRows,
            await ReadRuntimeRolePermissions(connection));
        Assert.Empty(await ReadDelegatedRuntimeRolePermissions(connection));

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT_BIG(*)
            FROM sys.database_role_members AS memberships
            INNER JOIN sys.database_principals AS roles
                ON roles.principal_id = memberships.role_principal_id
            INNER JOIN sys.database_principals AS members
                ON members.principal_id = memberships.member_principal_id
            WHERE roles.name = N'hotel_booking_runtime'
              AND members.name = N'agentic-api';
            """;
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadRuntimeRoleOwner(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT owners.name
            FROM sys.database_principals AS roles
            LEFT JOIN sys.database_principals AS owners
                ON owners.principal_id = roles.owning_principal_id
            WHERE roles.name = N'hotel_booking_runtime';
            """;
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<long> CountDirectPermissions(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions AS permissions
            INNER JOIN sys.database_principals AS principals
                ON principals.principal_id = permissions.grantee_principal_id
            WHERE principals.name = N'agentic-api';
            """;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountRuntimeRoles(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT_BIG(*)
            FROM sys.database_principals
            WHERE name = N'hotel_booking_runtime';
            """;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountRuntimeRolePermissions(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions AS permissions
            INNER JOIN sys.database_principals AS principals
                ON principals.principal_id = permissions.grantee_principal_id
            WHERE principals.name = N'hotel_booking_runtime';
            """;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ReadRuntimeRolePermissions(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CONCAT(
                permissions.class, N':',
                OBJECT_SCHEMA_NAME(permissions.major_id), N'.',
                OBJECT_NAME(permissions.major_id), N':',
                permissions.permission_name, N':',
                permissions.minor_id, N':',
                permissions.state)
            FROM sys.database_permissions AS permissions
            INNER JOIN sys.database_principals AS principals
                ON principals.principal_id = permissions.grantee_principal_id
            WHERE principals.name = N'hotel_booking_runtime'
            ORDER BY
                OBJECT_SCHEMA_NAME(permissions.major_id),
                OBJECT_NAME(permissions.major_id),
                permissions.permission_name;
            """;
        var permissions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            permissions.Add(reader.GetString(0));
        }

        return [.. permissions];
    }

    private static async Task<long> CountRuntimeRoleMembers(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT_BIG(*)
            FROM sys.database_role_members AS memberships
            INNER JOIN sys.database_principals AS roles
                ON roles.principal_id = memberships.role_principal_id
            WHERE roles.name = N'hotel_booking_runtime';
            """;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ReadDelegatedRuntimeRolePermissions(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CONCAT(
                permissions.class, N':',
                permissions.major_id, N':',
                permissions.minor_id, N':',
                permissions.permission_name COLLATE DATABASE_DEFAULT, N':',
                permissions.state COLLATE DATABASE_DEFAULT, N':',
                grantees.name COLLATE DATABASE_DEFAULT)
            FROM sys.database_permissions AS permissions
            INNER JOIN sys.database_principals AS grantees
                ON grantees.principal_id = permissions.grantee_principal_id
            WHERE permissions.class = 4
              AND permissions.major_id =
                  DATABASE_PRINCIPAL_ID(N'hotel_booking_runtime')
            ORDER BY
                permissions.permission_name,
                permissions.state,
                grantees.name;
            """;
        var permissions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            permissions.Add(reader.GetString(0));
        }

        return [.. permissions];
    }

    private static async Task<string[]> ReadDirectPermissions(
        SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CONCAT(
                permissions.class, N':',
                permissions.major_id, N':',
                permissions.minor_id, N':',
                permissions.permission_name, N':',
                permissions.state)
            FROM sys.database_permissions AS permissions
            INNER JOIN sys.database_principals AS principals
                ON principals.principal_id = permissions.grantee_principal_id
            WHERE principals.name = N'agentic-api'
            ORDER BY
                permissions.class,
                permissions.major_id,
                permissions.minor_id,
                permissions.permission_name,
                permissions.state;
            """;
        var permissions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            permissions.Add(reader.GetString(0));
        }

        return [.. permissions];
    }

    private static async Task ExecuteNonQuery(
        SqlConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static string DirectGrantsFor(string mutation) =>
        mutation switch
        {
            "subset" =>
                """
                GRANT SELECT ON OBJECT::dbo.Hotels TO [agentic-api];
                GRANT SELECT ON OBJECT::dbo.Rooms TO [agentic-api];
                GRANT SELECT, INSERT ON OBJECT::dbo.Reservations TO [agentic-api];
                GRANT SELECT, INSERT ON OBJECT::dbo.AgentEvents TO [agentic-api];
                """,
            "superset" =>
                ExactDirectGrants +
                "GRANT UPDATE ON OBJECT::dbo.Hotels TO [agentic-api];",
            "deny" =>
                ExactDirectGrants +
                "DENY SELECT ON OBJECT::dbo.Hotels TO [agentic-api];",
            "grant-option" =>
                ExactDirectGrants +
                "GRANT SELECT ON OBJECT::dbo.Hotels TO [agentic-api] WITH GRANT OPTION;",
            "column" =>
                """
                GRANT SELECT ON OBJECT::dbo.Hotels(Id) TO [agentic-api];
                GRANT SELECT ON OBJECT::dbo.Rooms TO [agentic-api];
                GRANT SELECT, INSERT ON OBJECT::dbo.Reservations TO [agentic-api];
                GRANT SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents TO [agentic-api];
                """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation),
                mutation,
                "Unsupported permission mutation."),
        };

    private const string ExactDirectGrants =
        """
        GRANT SELECT ON OBJECT::dbo.Hotels TO [agentic-api];
        GRANT SELECT ON OBJECT::dbo.Rooms TO [agentic-api];
        GRANT SELECT, INSERT ON OBJECT::dbo.Reservations TO [agentic-api];
        GRANT SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents TO [agentic-api];
        """;

    private static readonly string[] ExpectedRuntimePermissionRows =
    [
        "1:dbo.AgentEvents:DELETE:0:G",
        "1:dbo.AgentEvents:INSERT:0:G",
        "1:dbo.AgentEvents:SELECT:0:G",
        "1:dbo.Hotels:SELECT:0:G",
        "1:dbo.Reservations:INSERT:0:G",
        "1:dbo.Reservations:SELECT:0:G",
        "1:dbo.Rooms:SELECT:0:G",
    ];
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("SQL_BOOTSTRAP_TEST_CONNECTION")))
        {
            Skip =
                "SQL_BOOTSTRAP_TEST_CONNECTION is required for SQL Server bootstrap tests.";
        }
    }
}

public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("SQL_BOOTSTRAP_TEST_CONNECTION")))
        {
            Skip =
                "SQL_BOOTSTRAP_TEST_CONNECTION is required for SQL Server bootstrap tests.";
        }
    }
}

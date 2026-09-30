using AgenticHotelBooking.SqlManagedIdentityBootstrapper;
using Microsoft.Data.SqlClient;

namespace AgenticHotelBooking.UnitTests;

public sealed class SqlManagedIdentityBootstrapperTests
{
    private static readonly Guid PrincipalObjectId =
        Guid.Parse("3037fe2b-71f3-4322-9b80-e57c1e756a94");

    [Fact]
    public void ParsePreservesWorkflowSuppliedValuesAndExplicitToken()
    {
        var options = SqlBootstrapOptions.Parse(
            [
                "--server", "example.database.windows.net",
                "--database", "hotelbooking",
                "--principal-name", "agentic-api",
                "--principal-object-id", PrincipalObjectId.ToString(),
            ],
            name => name == "AZURE_SQL_ACCESS_TOKEN" ? "access-token" : null);

        Assert.Equal("example.database.windows.net", options.Server);
        Assert.Equal("hotelbooking", options.Database);
        Assert.Equal("agentic-api", options.PrincipalName);
        Assert.Equal(PrincipalObjectId, options.PrincipalObjectId);
        Assert.Equal("access-token", options.AccessToken);
    }

    [Theory]
    [InlineData("--server")]
    [InlineData("--database")]
    [InlineData("--principal-name")]
    [InlineData("--principal-object-id")]
    public void ParseRejectsEmptyRequiredInputs(string emptyOption)
    {
        var arguments = new[]
        {
            "--server", "example.database.windows.net",
            "--database", "hotelbooking",
            "--principal-name", "agentic-api",
            "--principal-object-id", PrincipalObjectId.ToString(),
        };
        arguments[Array.IndexOf(arguments, emptyOption) + 1] = string.Empty;

        Assert.Throws<ArgumentException>(
            () => SqlBootstrapOptions.Parse(arguments, _ => "access-token"));
    }

    [Fact]
    public void ParseRejectsEmptyAccessToken()
    {
        Assert.Throws<InvalidOperationException>(
            () => SqlBootstrapOptions.Parse(
                [
                    "--server", "example.database.windows.net",
                    "--database", "hotelbooking",
                    "--principal-name", "agentic-api",
                    "--principal-object-id", PrincipalObjectId.ToString(),
                ],
                _ => " "));
    }

    [Fact]
    public void ParseRequiresOutputOnlyForDiagnosticMode()
    {
        var diagnostic = SqlBootstrapOptions.Parse(
            [
                "--server", "example.database.windows.net",
                "--database", "hotelbooking",
                "--principal-name", "agentic-api",
                "--principal-object-id", PrincipalObjectId.ToString(),
                "--mode", "diagnostic",
                "--output", "evidence.json",
            ],
            _ => "access-token");

        Assert.Equal(SqlBootstrapMode.Diagnostic, diagnostic.Mode);
        Assert.Equal("evidence.json", diagnostic.DiagnosticOutputPath);
        Assert.Throws<ArgumentException>(() => SqlBootstrapOptions.Parse(
            [
                "--server", "example.database.windows.net",
                "--database", "hotelbooking",
                "--principal-name", "agentic-api",
                "--principal-object-id", PrincipalObjectId.ToString(),
                "--mode", "diagnostic",
            ],
            _ => "access-token"));
        Assert.Throws<ArgumentException>(() => SqlBootstrapOptions.Parse(
            [
                "--server", "example.database.windows.net",
                "--database", "hotelbooking",
                "--principal-name", "agentic-api",
                "--principal-object-id", PrincipalObjectId.ToString(),
                "--output", "evidence.json",
            ],
            _ => "access-token"));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("Bootstrap, Diagnostic")]
    public void ParseRejectsUndefinedOrNumericModes(string mode)
    {
        Assert.Throws<ArgumentException>(() => SqlBootstrapOptions.Parse(
            [
                "--server", "example.database.windows.net",
                "--database", "hotelbooking",
                "--principal-name", "agentic-api",
                "--principal-object-id", PrincipalObjectId.ToString(),
                "--mode", mode,
                "--output", "evidence.json",
            ],
            _ => "access-token"));
    }

    [Fact]
    public void CommandUsesParametersAndValidDynamicSqlExecution()
    {
        var options = CreateOptions();
        using var connection = SqlManagedIdentityBootstrap.CreateConnection(options);
        using var command = SqlManagedIdentityBootstrap.CreateCommand(connection, options);

        Assert.Equal("access-token", connection.AccessToken);
        Assert.Equal("example.database.windows.net", connection.DataSource);
        Assert.Equal("hotelbooking", connection.Database);
        Assert.Equal(
            ApplicationIntent.ReadWrite,
            new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationIntent);
        Assert.Equal("agentic-api", command.Parameters["@apiPrincipalName"].Value);
        Assert.Equal(PrincipalObjectId, command.Parameters["@apiPrincipalObjectId"].Value);
        Assert.Equal("E", command.Parameters["@apiPrincipalType"].Value);
        Assert.Equal("EXTERNAL", command.Parameters["@apiAuthenticationType"].Value);
        Assert.DoesNotContain(":setvar", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(N", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "EXEC sys.sp_executesql @Command",
            command.CommandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CommandFailsClosedOnUnexpectedPrincipalAccess()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;

        Assert.Contains("sys.database_permissions", commandText, StringComparison.Ordinal);
        Assert.Contains("unexpected direct database permissions", commandText, StringComparison.Ordinal);
        Assert.Contains(
            "name, SID, type, or authentication type does not match",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "Expected authentication type:",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "actual authentication type:",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "SET @ActualApiAuthenticationType = N'<ambiguous>'",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE name = @apiPrincipalName",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains("type = @apiPrincipalType", commandText, StringComparison.Ordinal);
        Assert.Contains(
            "authentication_type_desc = @apiAuthenticationType",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains("sys.database_role_members", commandText, StringComparison.Ordinal);
        Assert.Contains("unexpected database role memberships", commandText, StringComparison.Ordinal);
        Assert.Contains("sys.schemas", commandText, StringComparison.Ordinal);
        Assert.Contains("sys.objects", commandText, StringComparison.Ordinal);
        Assert.Contains("sys.databases", commandText, StringComparison.Ordinal);
        Assert.Contains("unexpectedly owns database securables", commandText, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandAllowsOnlyTheExactRuntimeRolePermissionSet()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;

        Assert.Contains("permissions.state = N'G'", commandText, StringComparison.Ordinal);
        Assert.Equal(
            4,
            CountOccurrences(
                commandText,
                "permissions.permission_name COLLATE DATABASE_DEFAULT"));
        Assert.Equal(
            4,
            CountOccurrences(
                commandText,
                "permissions.state COLLATE DATABASE_DEFAULT"));
        Assert.Contains("permissions.minor_id = 0", commandText, StringComparison.Ordinal);
        Assert.Contains("member_principal_id = @ExistingRuntimeRoleId", commandText, StringComparison.Ordinal);
        Assert.Contains("role_principal_id = @ExistingRuntimeRoleId", commandText, StringComparison.Ordinal);
        Assert.Contains("member_principal_id <> @ExistingApiPrincipalId", commandText, StringComparison.Ordinal);
        Assert.Contains(
            "runtime role has unexpected database principals as members",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains("unexpectedly nested", commandText, StringComparison.Ordinal);
        Assert.Contains("runtime role unexpectedly owns", commandText, StringComparison.Ordinal);
        Assert.Contains("runtime role has unexpected database permissions", commandText, StringComparison.Ordinal);
        Assert.Contains(
            "runtime role does not have the exact expected permission set",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG(*)", commandText, StringComparison.Ordinal);
        Assert.Contains("<> 7", commandText, StringComparison.Ordinal);
        Assert.Contains(
            "runtime role does not have the exact expected membership",
            commandText,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(
                commandText,
                "member_principal_id = @ExistingApiPrincipalId"));
        Assert.DoesNotContain(
            "IS_ROLEMEMBER",
            commandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryAllowsOnlyTheHistoricalRuntimePermissionContract()
    {
        var permissions = CreateRecoverableDirectPermissions();

        Assert.True(
            SqlManagedIdentityBootstrap.IsExactRecoverableDirectPermissionSet(permissions));
        Assert.Collection(
            SqlManagedIdentityBootstrap.RecoverableDirectPermissions,
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.Hotels", "SELECT"),
                permission),
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.Rooms", "SELECT"),
                permission),
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.Reservations", "SELECT"),
                permission),
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.Reservations", "INSERT"),
                permission),
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.AgentEvents", "SELECT"),
                permission),
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.AgentEvents", "INSERT"),
                permission),
            permission => Assert.Equal(
                new SqlObjectGrant("dbo.AgentEvents", "DELETE"),
                permission));
    }

    [Theory]
    [MemberData(nameof(RejectedRecoverablePermissionStates))]
    public void RecoveryRejectsMutatedPermissionStates(object value)
    {
        var permissions =
            Assert.IsAssignableFrom<IReadOnlyList<SqlDatabaseGrant>>(value);
        Assert.False(
            SqlManagedIdentityBootstrap.IsExactRecoverableDirectPermissionSet(permissions));
    }

    [Fact]
    public void CommandExcludesOnlyCanonicalConnectFromEveryDirectPermissionCheck()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;
        const string predicate =
            """
            permissions.class = 0
            AND permissions.major_id = 0
            AND permissions.minor_id = 0
            AND permissions.permission_name = N'CONNECT'
            AND permissions.state = N'G'
            AND permissions.grantor_principal_id = @DboPrincipalId
            """;
        var normalized = string.Join(
            '\n',
            commandText.Split('\n').Select(line => line.Trim()));
        Assert.Equal(
            4,
            CountOccurrences(
                normalized,
                "WHERE permissions.grantee_principal_id = @ExistingApiPrincipalId\nAND NOT (\n"
                    + predicate.ReplaceLineEndings("\n") + "\n)"));
        Assert.DoesNotContain("GRANT CONNECT", commandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REVOKE CONNECT", commandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandRevokesOnlyTheExactRuntimeContractAndVerifiesRemoval()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;

        Assert.Contains(
            "THROW 51000, 'The API principal has unexpected direct database permissions.'",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains("@ExistingDirectPermissionCount <> 7", commandText, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(commandText, "EXCEPT"));
        Assert.Contains("permissions.class = 1", commandText, StringComparison.Ordinal);
        Assert.Contains("permissions.minor_id = 0", commandText, StringComparison.Ordinal);
        Assert.Contains("permissions.state = N'G'", commandText, StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE SELECT ON OBJECT::dbo.Hotels FROM ",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE SELECT ON OBJECT::dbo.Rooms FROM ",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE SELECT, INSERT ON OBJECT::dbo.Reservations FROM ",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE SELECT, INSERT, DELETE ON OBJECT::dbo.AgentEvents FROM ",
            commandText,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(
                commandText,
                "EXEC sys.sp_executesql @Command;"));
        Assert.Contains(
            "still has direct database permissions after legacy migration",
            commandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DROP USER", commandText, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandFailsSafetyChecksBeforeLegacyPermissionMutation()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;
        var revokeIndex = commandText.IndexOf(
            "REVOKE SELECT ON OBJECT::dbo.Hotels",
            StringComparison.Ordinal);

        Assert.True(revokeIndex > 0);
        Assert.True(
            commandText.IndexOf(
                "name, SID, type, or authentication type does not match",
                StringComparison.Ordinal) < revokeIndex);
        Assert.True(
            commandText.IndexOf(
                "unexpected database role memberships",
                StringComparison.Ordinal) < revokeIndex);
        Assert.True(
            commandText.IndexOf(
                "unexpectedly owns database securables",
                StringComparison.Ordinal) < revokeIndex);
        Assert.True(
            commandText.IndexOf(
                "runtime role has unexpected database permissions",
                StringComparison.Ordinal) < revokeIndex);
        Assert.True(
            commandText.IndexOf(
                "runtime role has unexpected database principals as members",
                StringComparison.Ordinal) < revokeIndex);
        Assert.Contains(
            "@ExistingDirectPermissionCount > 0",
            commandText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CommandRejectsNullOrUnexpectedRuntimeRoleOwnerBeforeMutation()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;
        var ownerCheckIndex = commandText.IndexOf(
            "owning_principal_id IS NULL",
            StringComparison.Ordinal);
        var revokeIndex = commandText.IndexOf(
            "REVOKE SELECT ON OBJECT::dbo.Hotels",
            StringComparison.Ordinal);

        Assert.Contains(
            "DECLARE @DboPrincipalId int",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "owning_principal_id IS NULL",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "owning_principal_id <> @DboPrincipalId",
            commandText,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                commandText,
                "owning_principal_id <> @DboPrincipalId"));
        Assert.Contains(
            "The runtime role has an unexpected owner.",
            commandText,
            StringComparison.Ordinal);
        Assert.True(ownerCheckIndex > 0);
        Assert.True(ownerCheckIndex < revokeIndex);
        Assert.Contains(
            "CREATE ROLE [hotel_booking_runtime] AUTHORIZATION [dbo]",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "owning_principal_id = @DboPrincipalId",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "The runtime role does not have the canonical owner.",
            commandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER AUTHORIZATION",
            commandText,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DelegatedRuntimeRolePermissionStates))]
    public void ExtractedModelRejectsEveryExplicitRuntimeRolePermission(
        string permissionName,
        string state,
        int granteePrincipalId)
    {
        const int runtimeRolePrincipalId = 42;
        var permissions = new[]
        {
            new SqlDatabasePermissionEntry(
                4,
                runtimeRolePrincipalId,
                0,
                permissionName,
                state,
                granteePrincipalId),
        };

        Assert.False(
            SqlManagedIdentityBootstrap.HasNoExplicitPermissionsOnRuntimeRole(
                permissions,
                runtimeRolePrincipalId));
    }

    [Fact]
    public void ExtractedModelIgnoresPermissionsOnOtherSecurables()
    {
        var permissions = new[]
        {
            new SqlDatabasePermissionEntry(4, 41, 0, "ALTER", "G", 73),
            new SqlDatabasePermissionEntry(1, 42, 0, "CONTROL", "G", 73),
        };

        Assert.True(
            SqlManagedIdentityBootstrap.HasNoExplicitPermissionsOnRuntimeRole(
                permissions,
                42));
        Assert.True(
            SqlManagedIdentityBootstrap.HasNoExplicitPermissionsOnRuntimeRole(
                [],
                42));
    }

    [Fact]
    public void CommandRejectsDelegatedRuntimeRolePermissionsBeforeAndAfterMutation()
    {
        var commandText = SqlManagedIdentityBootstrap.CommandText;
        var precheckIndex = commandText.IndexOf(
            "The runtime role has delegated database-principal permissions.",
            StringComparison.Ordinal);
        var revokeIndex = commandText.IndexOf(
            "REVOKE SELECT ON OBJECT::dbo.Hotels",
            StringComparison.Ordinal);
        var postcheckIndex = commandText.IndexOf(
            "The runtime role has delegated database-principal permissions after bootstrap.",
            StringComparison.Ordinal);
        var membershipMutationIndex = commandText.IndexOf(
            "ALTER ROLE [hotel_booking_runtime] ADD MEMBER",
            StringComparison.Ordinal);

        Assert.Equal(2, CountOccurrences(commandText, "permissions.class = 4"));
        Assert.Contains(
            "permissions.major_id = @ExistingRuntimeRoleId",
            commandText,
            StringComparison.Ordinal);
        Assert.Contains(
            "permissions.major_id = @RuntimeRoleId",
            commandText,
            StringComparison.Ordinal);
        Assert.True(precheckIndex > 0);
        Assert.True(precheckIndex < revokeIndex);
        Assert.True(postcheckIndex > membershipMutationIndex);
    }

    [Fact]
    public void DiagnosticCommandIsParameterizedSelectOnlyAndReportsRequiredFacts()
    {
        var options = CreateOptions() with
        {
            Mode = SqlBootstrapMode.Diagnostic,
            DiagnosticOutputPath = "evidence.json",
        };
        using var connection = SqlManagedIdentityBootstrap.CreateConnection(options);
        using var command =
            SqlManagedIdentityBootstrap.CreateDiagnosticCommand(connection, options);

        Assert.Equal(
            ApplicationIntent.ReadOnly,
            new SqlConnectionStringBuilder(connection.ConnectionString).ApplicationIntent);
        Assert.Equal("agentic-api", command.Parameters["@apiPrincipalName"].Value);
        Assert.Equal(PrincipalObjectId, command.Parameters["@apiPrincipalObjectId"].Value);
        Assert.Contains("identityCandidates", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("observer.isDatabaseOwner", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("observer.canViewDefinition", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("directPermissions", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("permissions.state", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("permissions.class", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("schemaName", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("objectName", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("permissions.permission_name", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("grantor", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("roleMemberships", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("roleOwnership", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("delegatedPermissions", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("ownedSecurables", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("FROM sys.databases AS databases", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("databases.owner_sid", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("@@TRANCOUNT", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER", command.CommandText, StringComparison.Ordinal);

        foreach (var mutationVerb in new[]
                 {
                     "ALTER", "CREATE", "DELETE", "DROP", "EXEC", "GRANT",
                     "INSERT", "MERGE", "REVOKE", "TRUNCATE", "UPDATE",
                 })
        {
            Assert.DoesNotMatch(
                $@"(?im)\b{mutationVerb}\b",
                command.CommandText);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"target":{}}""")]
    [InlineData(
        """
        {
          "target": {"principalName":"","principalObjectId":"not-a-guid"},
          "databaseName":"hotelbooking",
          "observer": {},
          "transactionCount":0,
          "identityCandidates":[],
          "directPermissions":[],
          "roleMemberships":[],
          "roleOwnership":[],
          "delegatedPermissions":[],
          "ownedSecurables":[]
        }
        """)]
    public void DiagnosticJsonValidationRejectsIncompleteEvidence(string json)
    {
        Assert.Throws<InvalidDataException>(
            () => SqlManagedIdentityBootstrap.ValidateDiagnosticJson(json));
    }

    [Fact]
    public void DiagnosticWorkflowIsDevelopmentOnlyExactIpAndAlwaysCleansUp()
    {
        ValidateDiagnosticWorkflow(ReadDiagnosticWorkflow());
    }

    [Theory]
    [InlineData(
        "environment: development",
        "environment: production")]
    [InlineData(
        "--start-ip-address '${{ steps.target.outputs.runnerIp }}'",
        "--start-ip-address 0.0.0.0")]
    [InlineData(
        "--end-ip-address '${{ steps.target.outputs.runnerIp }}'",
        "--end-ip-address 255.255.255.255")]
    [InlineData(
        "if: always() && steps.target.outcome == 'success'",
        "if: success() && steps.target.outcome == 'success'")]
    [InlineData(
        "--name '${{ steps.target.outputs.ruleName }}'",
        "--name shared-diagnostic-rule")]
    [InlineData(
        "inputs.confirmation == 'DIAGNOSE-DEVELOPMENT-SQL'",
        "inputs.confirmation != ''")]
    [InlineData(
        "github.ref == 'refs/heads/main'",
        "github.ref != ''")]
    public void DiagnosticWorkflowPolicyRejectsSafetyMutations(
        string original,
        string mutation)
    {
        var baseline = ReadDiagnosticWorkflow();
        Assert.Contains(original, baseline, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(
            () => ValidateDiagnosticWorkflow(
                baseline.Replace(original, mutation, StringComparison.Ordinal)));
    }

    public static TheoryData<object>
        RejectedRecoverablePermissionStates()
    {
        var exact = CreateRecoverableDirectPermissions();
        return new TheoryData<object>
        {
            exact.Take(exact.Count - 1).ToArray(),
            exact.Append(new("dbo.Hotels", "UPDATE")).ToArray(),
            exact.Select(
                    (permission, index) => index == 0
                        ? permission with { State = "W" }
                        : permission)
                .ToArray(),
            exact.Select(
                    (permission, index) => index == 0
                        ? permission with { State = "D" }
                        : permission)
                .ToArray(),
            exact.Select(
                    (permission, index) => index == 0
                        ? permission with { MinorId = 1 }
                        : permission)
                .ToArray(),
            exact.Select(
                    (permission, index) => index == 0
                        ? permission with { Class = 0 }
                        : permission)
                .ToArray(),
            exact.Select(
                    (permission, index) => index == 0
                        ? permission with { ObjectName = "dbo.Users" }
                        : permission)
                .ToArray(),
            exact.Take(exact.Count - 1).Append(exact[0]).ToArray(),
        };
    }

    public static TheoryData<string, string, int>
        DelegatedRuntimeRolePermissionStates()
    {
        var states = new TheoryData<string, string, int>();
        foreach (var permissionName in new[] { "ALTER", "CONTROL", "TAKE OWNERSHIP" })
        {
            foreach (var state in new[] { "G", "W" })
            {
                states.Add(permissionName, state, 73);
                states.Add(permissionName, state, 74);
            }
        }

        return states;
    }

    private static IReadOnlyList<SqlDatabaseGrant>
        CreateRecoverableDirectPermissions() =>
        [
            new("dbo.Hotels", "SELECT"),
            new("dbo.Rooms", "SELECT"),
            new("dbo.Reservations", "SELECT"),
            new("dbo.Reservations", "INSERT"),
            new("dbo.AgentEvents", "SELECT"),
            new("dbo.AgentEvents", "INSERT"),
            new("dbo.AgentEvents", "DELETE"),
        ];

    private static int CountOccurrences(string value, string expected) =>
        (value.Length - value.Replace(expected, string.Empty, StringComparison.Ordinal).Length)
        / expected.Length;

    private static string ReadDiagnosticWorkflow() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            ".github",
            "workflows",
            "diagnose-development-sql.yml"));

    private static void ValidateDiagnosticWorkflow(string workflow)
    {
        var requiredFragments = new[]
        {
            "workflow_dispatch:",
            "github.ref == 'refs/heads/main'",
            "inputs.confirmation == 'DIAGNOSE-DEVELOPMENT-SQL'",
            "environment: development",
            "RESOURCE_GROUP: agentic-hotelbookingdev",
            "DATABASE_NAME: hotelbooking",
            "--start-ip-address '${{ steps.target.outputs.runnerIp }}'",
            "--end-ip-address '${{ steps.target.outputs.runnerIp }}'",
            "--name '${{ steps.target.outputs.ruleName }}'",
            "if: always() && steps.target.outcome == 'success'",
            "az sql server firewall-rule delete",
            "The exact diagnostic firewall rule remains after cleanup.",
            "--mode diagnostic",
            "uses: actions/upload-artifact@v4",
        };
        foreach (var fragment in requiredFragments)
        {
            if (!workflow.Contains(fragment, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Diagnostic workflow is missing required safety fragment: {fragment}");
            }
        }

        var forbiddenFragments = new[]
        {
            "environment: production",
            "--start-ip-address 0.0.0.0",
            "--end-ip-address 255.255.255.255",
            "--name shared-diagnostic-rule",
            "if: success() && steps.target.outcome == 'success'",
            "inputs.confirmation != ''",
            "github.ref != ''",
            "az deployment ",
            "dotnet ef ",
        };
        foreach (var fragment in forbiddenFragments)
        {
            if (workflow.Contains(fragment, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Diagnostic workflow contains forbidden fragment: {fragment}");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgenticHotelBooking.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private static SqlBootstrapOptions CreateOptions() =>
        new(
            "example.database.windows.net",
            "hotelbooking",
            "agentic-api",
            PrincipalObjectId,
            "access-token");
}

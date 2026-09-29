using AgenticHotelBooking.SqlManagedIdentityBootstrapper;

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
    public void CommandUsesParametersAndValidDynamicSqlExecution()
    {
        var options = CreateOptions();
        using var connection = SqlManagedIdentityBootstrap.CreateConnection(options);
        using var command = SqlManagedIdentityBootstrap.CreateCommand(connection, options);

        Assert.Equal("access-token", connection.AccessToken);
        Assert.Equal("example.database.windows.net", connection.DataSource);
        Assert.Equal("hotelbooking", connection.Database);
        Assert.Equal("agentic-api", command.Parameters["@apiPrincipalName"].Value);
        Assert.Equal(PrincipalObjectId, command.Parameters["@apiPrincipalObjectId"].Value);
        Assert.DoesNotContain(":setvar", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC(N", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "EXEC sys.sp_executesql @Command",
            command.CommandText,
            StringComparison.Ordinal);
    }

    private static SqlBootstrapOptions CreateOptions() =>
        new(
            "example.database.windows.net",
            "hotelbooking",
            "agentic-api",
            PrincipalObjectId,
            "access-token");
}

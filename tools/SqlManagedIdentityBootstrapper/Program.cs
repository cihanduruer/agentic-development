using AgenticHotelBooking.SqlManagedIdentityBootstrapper;

var options = SqlBootstrapOptions.Parse(
    args,
    Environment.GetEnvironmentVariable);

await using var connection = SqlManagedIdentityBootstrap.CreateConnection(options);
await connection.OpenAsync();

if (options.Mode == SqlBootstrapMode.Diagnostic)
{
    await using var command =
        SqlManagedIdentityBootstrap.CreateDiagnosticCommand(connection, options);
    var json = await SqlManagedIdentityBootstrap.ExecuteDiagnosticAsync(command);
    await File.WriteAllTextAsync(options.DiagnosticOutputPath!, json);
    Console.WriteLine("Wrote sanitized SQL permission diagnostic evidence.");
}
else
{
    await using var command = SqlManagedIdentityBootstrap.CreateCommand(connection, options);
    await command.ExecuteNonQueryAsync();

    Console.WriteLine(
        $"Bootstrapped managed identity '{options.PrincipalName}' in database '{options.Database}'.");
}

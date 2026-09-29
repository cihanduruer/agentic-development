using AgenticHotelBooking.SqlManagedIdentityBootstrapper;

var options = SqlBootstrapOptions.Parse(
    args,
    Environment.GetEnvironmentVariable);

await using var connection = SqlManagedIdentityBootstrap.CreateConnection(options);
await connection.OpenAsync();

await using var command = SqlManagedIdentityBootstrap.CreateCommand(connection, options);
await command.ExecuteNonQueryAsync();

Console.WriteLine(
    $"Bootstrapped managed identity '{options.PrincipalName}' in database '{options.Database}'.");

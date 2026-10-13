using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgenticHotelBooking.UnitTests;

public sealed class DotnetSdkPinContractTests
{
    [Fact]
    public void GlobalJsonPinsTheSdkWithoutRollForward()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("global.json")));
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", sdk.GetProperty("version").GetString());
    }

    [Fact]
    public void EveryWorkflowInstallsExactlyTheGlobalJsonSdk()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("global.json")));
        var pinned = document.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        var workflows = Directory.GetFiles(Path.GetDirectoryName(FindRepositoryFile(".github/workflows/pr-validation.yml"))!, "*.yml");

        var versions = workflows
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"dotnet-version:\s*(\S+)").Select(match => (path, version: match.Groups[1].Value)))
            .ToArray();

        Assert.NotEmpty(versions);
        Assert.All(versions, entry => Assert.True(
            entry.version == pinned,
            $"{Path.GetFileName(entry.path)} uses dotnet-version {entry.version}; expected {pinned} from global.json."));
    }

    private static string FindRepositoryFile(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, path.Replace('/', Path.DirectorySeparatorChar));
    }
}

using System.Diagnostics;
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

    [Fact]
    public void WebAssemblySdkPackPinMatchesTheLockFile()
    {
        var repositoryRoot = Path.GetDirectoryName(FindRepositoryFile("global.json"))!;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add("src/Web/AgenticHotelBooking.Web.csproj");
        startInfo.ArgumentList.Add("-getItem:KnownWebAssemblySdkPack");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Could not read the pinned WebAssembly pack: {error}");

        using var sdkDocument = JsonDocument.Parse(output);
        var sdkPackVersion = Assert.Single(
            sdkDocument.RootElement
                .GetProperty("Items")
                .GetProperty("KnownWebAssemblySdkPack")
                .EnumerateArray(),
            item => item.GetProperty("TargetFramework").GetString() == "net10.0")
            .GetProperty("WebAssemblySdkPackVersion")
            .GetString();
        Assert.NotNull(sdkPackVersion);
        Assert.Equal("10.0.13", sdkPackVersion);

        using var lockDocument = JsonDocument.Parse(
            File.ReadAllText(FindRepositoryFile("src/Web/packages.lock.json")));
        var lockedPack = lockDocument.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0")
            .GetProperty("Microsoft.NET.Sdk.WebAssembly.Pack");

        Assert.Equal(sdkPackVersion, lockedPack.GetProperty("resolved").GetString());
        Assert.Equal($"[{sdkPackVersion}, )", lockedPack.GetProperty("requested").GetString());
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

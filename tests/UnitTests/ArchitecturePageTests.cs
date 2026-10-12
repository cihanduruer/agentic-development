namespace AgenticHotelBooking.UnitTests;

public sealed class ArchitecturePageTests
{
    [Fact]
    public void ArchitecturePageExplainsEachFlowWithOrderedTextAndAccessibleLabels()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");

        Assert.Contains("@page \"/architecture\"", page);
        Assert.Contains("<h1 id=\"architecture-title\">How Hotel works</h1>", page);
        Assert.Contains("Guest booking request flow", page);
        Assert.Contains("Runtime routing flow", page);
        Assert.Contains("Live operations event flow", page);
        Assert.Contains("Software delivery flow", page);
        Assert.Equal(4, Count(page, "<ol class=\"flow-track\""));
        Assert.Contains("The browser does not connect directly to the database.", page);
        Assert.Contains("An accepted event is saved before the API broadcasts it", page);
        Assert.Contains("human approval and a manual promotion", page);
        Assert.Contains("This is a system overview, not a live traffic monitor.", page);
    }

    [Fact]
    public void ArchitectureMotionIsDecorativeAndRespectsReducedMotion()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");
        var styles = ReadSource("src/Web/Pages/Architecture.razor.css");

        Assert.Contains("Moving dots are decorative", page);
        Assert.Contains("aria-hidden=\"true\"", page);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles);
        Assert.Contains("animation: none;", styles);
        Assert.Contains("@media (max-width: 800px)", styles);
    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}'.");
    }
}

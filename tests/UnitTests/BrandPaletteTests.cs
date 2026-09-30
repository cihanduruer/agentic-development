using System.Globalization;

namespace AgenticHotelBooking.UnitTests;

public sealed class BrandPaletteTests
{
    private static readonly Dictionary<string, string> Palette = new()
    {
        ["--brand-dark-blue"] = "#164A61",
        ["--brand-blue"] = "#04668C",
        ["--brand-light-blue"] = "#8DC4E6",
        ["--brand-cream"] = "#F9F6EF",
        ["--brand-taupe"] = "#D4CDBF"
    };

    [Fact]
    public void SharedStylesDefineTheExactBrandPalette()
    {
        var css = File.ReadAllText(FindStylesheet());

        foreach (var token in Palette)
        {
            Assert.Contains($"{token.Key}: {token.Value};", css, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--brand-dark-blue", "--brand-cream", 4.5)]
    [InlineData("--brand-blue", "--brand-cream", 4.5)]
    [InlineData("--brand-dark-blue", "--brand-light-blue", 4.5)]
    public void TextTokenPairsMeetWcagAaContrast(string foregroundToken, string backgroundToken, double minimum)
    {
        var foreground = Hex(Palette[foregroundToken]);
        var background = Hex(Palette[backgroundToken]);
        var ratio = (Luminance(foreground) + 0.05) / (Luminance(background) + 0.05);

        Assert.True(ratio >= minimum, $"{foregroundToken} on {backgroundToken} has contrast {ratio:F2}:1.");
    }

    private static string FindStylesheet()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Web", "wwwroot", "css", "app.css");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the Hotel application stylesheet.");
    }

    private static (byte Red, byte Green, byte Blue) Hex(string value) =>
        (byte.Parse(value[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(value[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(value[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static double Luminance((byte Red, byte Green, byte Blue) color) =>
        new[] { color.Red, color.Green, color.Blue }
            .Select(channel =>
            {
                var normalized = channel / 255d;
                return normalized <= 0.03928 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            })
            .Select((channel, index) => channel * new[] { 0.2126, 0.7152, 0.0722 }[index])
            .Sum();
}

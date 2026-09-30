using System.Globalization;
using System.Text.RegularExpressions;

namespace AgenticHotelBooking.UnitTests;

public sealed class BrandPaletteTests
{
    private static readonly double[] LuminanceWeights = [0.2126, 0.7152, 0.0722];
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
        var css = ReadSource("src/Web/wwwroot/css/app.css");

        foreach (var token in Palette)
        {
            Assert.Contains($"{token.Key}: {token.Value};", css, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("html", "color", "background", 4.5)]
    [InlineData(".room-option", "color", "background", 4.5)]
    [InlineData(".empty-state", "color", "background", 4.5)]
    [InlineData(".alert-danger", "color", "background-color", 4.5)]
    [InlineData(".btn-primary", "--bs-btn-color", "--bs-btn-bg", 4.5)]
    [InlineData(".btn-primary", "--bs-btn-hover-color", "--bs-btn-hover-bg", 4.5)]
    [InlineData(".btn-primary", "--bs-btn-active-color", "--bs-btn-active-bg", 4.5)]
    [InlineData(".btn-primary", "--bs-btn-disabled-color", "--bs-btn-disabled-bg", 4.5)]
    [InlineData(".btn-outline-primary", "--bs-btn-color", "--bs-btn-bg", 4.5)]
    public void ImplementedColorPairsMeetWcagAaContrast(
        string selector, string foregroundProperty, string backgroundProperty, double minimum)
    {
        var css = ReadSource("src/Web/wwwroot/css/app.css");
        var foreground = Hex(ResolveToken(css, Declaration(css, selector, foregroundProperty)));
        var background = Hex(ResolveToken(css, Declaration(css, selector, backgroundProperty)));
        var foregroundLuminance = Luminance(foreground) + 0.05;
        var backgroundLuminance = Luminance(background) + 0.05;
        var ratio = Math.Max(foregroundLuminance, backgroundLuminance) /
                    Math.Min(foregroundLuminance, backgroundLuminance);

        Assert.True(ratio >= minimum, $"{selector} has contrast {ratio:F2}:1.");
    }

    [Fact]
    public void InteractionRulesUseHighContrastTokensAndNonColorSelectionCues()
    {
        var css = ReadSource("src/Web/wwwroot/css/app.css");
        Assert.Equal("1px solid var(--brand-blue)", Declaration(css, ".room-option", "border"));
        Assert.Equal("1px solid var(--brand-dark-blue)", Declaration(css, ".booking-fields input", "border"));
        Assert.Equal("3px solid var(--brand-blue)", Declaration(css, "input:focus-visible", "outline"));
        Assert.Equal("3px solid var(--brand-blue)", Declaration(css, ".btn:active:focus-visible", "outline"));
        Assert.Equal("0 0 0 3px var(--brand-cream)", Declaration(css, "button:focus-visible", "box-shadow"));
        Assert.Equal("1", Declaration(css, ".btn-primary", "--bs-btn-disabled-opacity"));
        Assert.Equal("var(--brand-light-blue)", Declaration(css, ".room-option.selected", "background"));
        Assert.NotEqual(Declaration(css, ".room-option.selected", "background"),
            Declaration(css, ".room-option:hover", "background"));
        var home = ReadSource("src/Web/Pages/Home.razor");
        Assert.Contains("aria-pressed=", home);
        Assert.Contains("<span class=\"room-selection\">Selected</span>", home);

        var navigation = ReadSource("src/Web/Layout/NavMenu.razor.css");
        Assert.Equal("currentColor", Declaration(navigation, ".bi-house-door-fill-nav-menu", "background-color"));
        Assert.Equal("underline", Declaration(navigation, ".nav-item ::deep a.active", "text-decoration"));
        Assert.Equal("3px solid var(--brand-cream)", Declaration(navigation, ".nav-item ::deep a:focus-visible", "outline"));
        Assert.Contains("<button type=\"button\" class=\"dismiss\" aria-label=\"Dismiss error\">",
            ReadSource("src/Web/wwwroot/index.html"));
    }

    [Fact]
    public void VehiclePreferenceControlIsLabeledAndExplainsItsLimits()
    {
        var home = ReadSource("src/Web/Pages/Home.razor");

        Assert.Contains("<label for=\"vehicle-preference\">Vehicle preference</label>", home);
        Assert.Contains("aria-describedby=\"vehicle-preference-help\"", home);
        Assert.Contains("<option value=\"\">No preference</option>", home);
        Assert.Contains("A vehicle preference is not a guaranteed rental.", home);
        Assert.Contains("cannot be added or changed after confirmation", home);

        var css = ReadSource("src/Web/wwwroot/css/app.css");
        Assert.Equal("1px solid var(--brand-dark-blue)",
            Declaration(css, ".vehicle-preference select", "border"));
        Assert.Equal("3px solid var(--brand-blue)", Declaration(css, "select:focus-visible", "outline"));
    }

    private static string Declaration(string css, string selector, string property)
    {
        var values = Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")
            .Where(rule => rule.Groups["selectors"].Value.Split(',').Any(value => value.Trim() == selector))
            .SelectMany(rule => rule.Groups["body"].Value.Split(';'))
            .Select(value => value.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Trim() == property)
            .Select(parts => parts[1].Trim())
            .ToArray();
        Assert.NotEmpty(values);
        return values[^1];
    }

    private static string ResolveToken(string css, string value)
    {
        var match = Regex.Match(value, @"^var\((--brand-[a-z-]+)\)$");
        Assert.True(match.Success, $"Expected a shared brand token, got '{value}'.");
        return Declaration(css, ":root", match.Groups[1].Value);
    }

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
            .Select((channel, index) => channel * LuminanceWeights[index])
            .Sum();
}

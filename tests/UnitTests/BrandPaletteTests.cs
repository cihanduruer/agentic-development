using System.Globalization;
using System.Text.RegularExpressions;

namespace AgenticHotelBooking.UnitTests;

public sealed class BrandPaletteTests
{
    private static readonly double[] LuminanceWeights = [0.2126, 0.7152, 0.0722];
    private static readonly Dictionary<string, string> Palette = new()
    {
        ["--brand-ink"] = "#2B0A0D",
        ["--brand-crimson"] = "#C4000C",
        ["--brand-blush"] = "#FFD6D9",
        ["--brand-cream"] = "#F9F6EF",
        ["--brand-taupe"] = "#D4CDBF",
        ["--brand-red"] = "#FF000F"
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

    [Fact]
    public void HomePageTextMeetsContrastOnItsBackground()
    {
        var css = ReadSource("src/Web/wwwroot/css/app.css");
        var homeCss = ReadSource("src/Web/Pages/Home.razor.css");
        var home = ReadSource("src/Web/Pages/Home.razor");

        Assert.Contains("<div class=\"home-page\">", home);
        var background = Hex(ResolveToken(css, Declaration(homeCss, ".home-page", "background")));
        var foreground = Hex(ResolveToken(css, Declaration(css, "body", "color")));
        var foregroundLuminance = Luminance(foreground) + 0.05;
        var backgroundLuminance = Luminance(background) + 0.05;
        var contrast = Math.Max(foregroundLuminance, backgroundLuminance) /
                       Math.Min(foregroundLuminance, backgroundLuminance);

        Assert.True(contrast >= 4.5, $"Home page text has contrast {contrast:F2}:1.");
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
    public void BrandRedIsOnlyUsedWithInkTextBecauseCreamOnRedFailsContrast()
    {
        var css = ReadSource("src/Web/wwwroot/css/app.css");
        var homeCss = ReadSource("src/Web/Pages/Home.razor.css");
        var red = Hex(ResolveToken(css, "var(--brand-red)"));
        var ink = Hex(ResolveToken(css, "var(--brand-ink)"));
        var cream = Hex(ResolveToken(css, "var(--brand-cream)"));

        Assert.True(Ratio(ink, red) >= 4.5, $"Ink on brand red has contrast {Ratio(ink, red):F2}:1.");
        Assert.True(Ratio(cream, red) < 4.5, "Cream on brand red now passes; the ink restriction can be relaxed.");
        foreach (var property in new[] { "--bs-btn-color", "--bs-btn-hover-color", "--bs-btn-disabled-color" })
        {
            Assert.Equal("var(--brand-ink)", Declaration(homeCss, ".search-fields .search-submit", property));
        }

        Assert.Equal("var(--brand-red)", Declaration(homeCss, ".search-fields .search-submit", "--bs-btn-bg"));
        Assert.Equal("var(--brand-taupe)", Declaration(homeCss, ".search-fields .search-submit", "--bs-btn-disabled-bg"));
        Assert.Equal("var(--brand-cream)", Declaration(homeCss, ".search-fields .search-submit", "--bs-btn-active-color"));
        Assert.Equal("var(--brand-ink)", Declaration(homeCss, ".search-fields .search-submit", "--bs-btn-active-bg"));
        Assert.True(Ratio(cream, ink) >= 4.5, "The pressed search button needs AA contrast.");
    }

    [Fact]
    public void InteractionRulesUseHighContrastTokensAndNonColorSelectionCues()
    {
        var css = ReadSource("src/Web/wwwroot/css/app.css");
        Assert.Equal("1px solid var(--brand-crimson)", Declaration(css, ".room-option", "border"));
        Assert.Equal("1px solid var(--brand-ink)", Declaration(css, ".booking-fields input", "border"));
        Assert.Equal("1px solid var(--brand-ink)", Declaration(css, ".booking-fields select", "border"));
        Assert.Equal("3px solid var(--brand-crimson)", Declaration(css, "input:focus-visible", "outline"));
        Assert.Equal("3px solid var(--brand-crimson)", Declaration(css, ".btn:active:focus-visible", "outline"));
        Assert.Equal("0 0 0 3px var(--brand-cream)", Declaration(css, "button:focus-visible", "box-shadow"));
        Assert.Equal("1", Declaration(css, ".btn-primary", "--bs-btn-disabled-opacity"));
        Assert.Equal("var(--brand-blush)", Declaration(css, ".room-option.selected", "background"));
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
        Assert.Equal("1px solid var(--brand-ink)",
            Declaration(css, ".vehicle-preference select", "border"));
        Assert.Equal("3px solid var(--brand-crimson)", Declaration(css, "select:focus-visible", "outline"));
    }

    [Fact]
    public void HomePagePutsHotelAndStaySearchControlsBeforeTheCatalog()
    {
        var home = ReadSource("src/Web/Pages/Home.razor");
        var searchPanel = home.IndexOf("<section class=\"search-panel\"", StringComparison.Ordinal);
        var catalog = home.IndexOf("<div class=\"hotel-grid\">", StringComparison.Ordinal);

        Assert.True(searchPanel >= 0 && catalog > searchPanel);
        Assert.Contains("for=\"hotel-destination\"", home);
        Assert.Contains("Hotel or destination", home);
        Assert.Contains("for=\"check-in\"", home);
        Assert.Contains("for=\"check-out\"", home);
        Assert.Contains("for=\"guest-count\"", home);
        Assert.Contains("class=\"btn btn-primary search-submit\"", home);
        Assert.Contains("SelectHotel(hotels[0])", home);
        Assert.Contains("@bind:after=\"OnHotelSelectionChanged\"", home);

        var css = ReadSource("src/Web/Pages/Home.razor.css");
        Assert.Contains("grid-template-columns: minmax(220px, 2fr)", css);
        Assert.Contains("@media (max-width: 1180px)", css);
        Assert.Contains("@media (max-width: 480px)", css);
    }

    [Fact]
    public void AmenityFiltersAreCatalogDrivenLabeledAndClearable()
    {
        var home = ReadSource("src/Web/Pages/Home.razor");

        Assert.Contains("selectedHotel?.Rooms", home);
        Assert.Contains("Choose one or more amenities. Rooms must include every selected amenity.", home);
        Assert.Contains("<fieldset class=\"amenity-filters\" aria-describedby=\"amenity-filter-help\">", home);
        Assert.Contains("<legend>Filter available rooms by amenities</legend>", home);
        Assert.Contains("type=\"checkbox\"", home);
        Assert.Contains("for=\"@inputId\"", home);
        Assert.Contains(">Clear all filters</button>", home);
        Assert.Contains("Available rooms were found, but none match all selected amenities.", home);
        Assert.Contains("No rooms are available for these dates and guest count.", home);
        Assert.Contains("RoomAmenityFilter.MatchAll(rooms ?? [], selectedAmenities)", home);
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

    private static double Ratio((byte Red, byte Green, byte Blue) first, (byte Red, byte Green, byte Blue) second)
    {
        var a = Luminance(first) + 0.05;
        var b = Luminance(second) + 0.05;
        return Math.Max(a, b) / Math.Min(a, b);
    }
}

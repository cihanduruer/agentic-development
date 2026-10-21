namespace AgenticHotelBooking.UnitTests;

public sealed class AvailabilityPageTests
{
    [Fact]
    public void HomeCarriesTheSelectedStayToTheDedicatedAvailabilityRoute()
    {
        var home = ReadSource("src/Web/Pages/Home.razor");

        Assert.Contains("@page \"/\"", home);
        Assert.Contains("@onclick=\"SearchAvailabilityFromForm\"", home);
        Assert.Contains(
            $"/availability?hotelId={{Uri.EscapeDataString(destinationHotelId)}}&checkIn={{checkIn:yyyy-MM-dd}}&checkOut={{checkOut:yyyy-MM-dd}}&guests={{guests}}",
            home);
        Assert.DoesNotContain("booking-panel", home);
        Assert.DoesNotContain("api/hotels/{hotelId}/availability", home);
    }

    [Fact]
    public void AvailabilityPageDisplaysTheSubmittedValuesAndValidatesBeforeSearching()
    {
        var availability = ReadSource("src/Web/Pages/Availability.razor");

        Assert.Contains("@page \"/availability\"", availability);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"checkIn\")]", availability);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"checkOut\")]", availability);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"guests\")]", availability);
        Assert.Contains("@(CheckInQuery ?? \"Not provided\")", availability);
        Assert.Contains("@(CheckOutQuery ?? \"Not provided\")", availability);
        Assert.Contains("@(GuestsQuery ?? \"Not provided\")", availability);
        Assert.Contains("Check-in cannot be in the past.", availability);
        Assert.Contains("Check-out must be after check-in.", availability);
        Assert.Contains("Choose between 1 and 8 guests.", availability);
        AssertOrdered(
            availability,
            "rooms = null;",
            "if (!DateOnly.TryParseExact(",
            "if (checkIn < DateOnly.FromDateTime(DateTime.Today))",
            "if (checkOut <= checkIn)",
            "Http.GetFromJsonAsync<Room[]>(");
    }

    private static void AssertOrdered(string source, params string[] values)
    {
        var positions = values.Select(value => source.IndexOf(value, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order().ToArray(), positions);
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
}

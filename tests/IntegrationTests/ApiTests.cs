using System.Net;
using System.Net.Http.Json;
using AgenticHotelBooking.Application;
using AgenticHotelBooking.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void RuntimeMigrationPolicyRequiresExplicitDevelopmentOptIn(
        bool isDevelopment,
        bool configured,
        bool expected)
    {
        Assert.Equal(expected, Program.ShouldApplyDatabaseMigrations(isDevelopment, configured));
    }

    [Fact]
    public async Task HealthReturnsHealthy()
    {
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HotelsReturnsSeededCatalog()
    {
        var response = await client.GetAsync("/api/hotels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Canal House", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReservationResponseIncludesMultiNightTotal()
    {
        var hotels = await client.GetFromJsonAsync<Hotel[]>("/api/hotels");
        var hotel = hotels![0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20));

        var response = await client.PostAsJsonAsync("/api/reservations",
            new BookingRequest(hotel.Id, room.Id, checkIn, checkIn.AddDays(2), 2, "Ada"));
        var reservation = await response.Content.ReadFromJsonAsync<Reservation>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, reservation!.Nights);
        Assert.Equal(room.NightlyRate * 2, reservation.TotalStayPrice);
        Assert.Null(reservation.VehiclePreference);
    }

    [Fact]
    public async Task ReservationPersistsVehiclePreferenceAndRejectsLaterChanges()
    {
        var hotels = await client.GetFromJsonAsync<Hotel[]>("/api/hotels");
        var hotel = hotels![0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60));

        var response = await client.PostAsJsonAsync(
            "/api/reservations",
            new BookingRequest(
                hotel.Id,
                room.Id,
                checkIn,
                checkIn.AddDays(2),
                2,
                "Grace",
                VehiclePreference.SUV));
        var reservation = await response.Content.ReadFromJsonAsync<Reservation>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(VehiclePreference.SUV, reservation!.VehiclePreference);

        var changeResponse = await client.PatchAsJsonAsync(
            response.Headers.Location,
            new { vehiclePreference = VehiclePreference.Luxury });
        var error = await changeResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, changeResponse.StatusCode);
        Assert.Contains("Confirmed reservations cannot be changed.", error);
    }

    [Fact]
    public async Task ReservationRejectsUnknownVehiclePreference()
    {
        var hotels = await client.GetFromJsonAsync<Hotel[]>("/api/hotels");
        var hotel = hotels![0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(80));

        var request = $$"""
            {
              "hotelId": "{{hotel.Id}}",
              "roomId": "{{room.Id}}",
              "checkIn": "{{checkIn:yyyy-MM-dd}}",
              "checkOut": "{{checkIn.AddDays(1):yyyy-MM-dd}}",
              "guests": 1,
              "guestName": "Katherine",
              "vehiclePreference": 99
            }
            """;
        var response = await client.PostAsync(
            "/api/reservations",
            new StringContent(request, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The request body is invalid.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReservationAcceptsNamedVehiclePreferenceAndRejectsUnknownName()
    {
        var hotels = await client.GetFromJsonAsync<Hotel[]>("/api/hotels");
        var hotel = hotels![0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(100));
        var request = $$"""
            {
              "hotelId": "{{hotel.Id}}",
              "roomId": "{{room.Id}}",
              "checkIn": "{{checkIn:yyyy-MM-dd}}",
              "checkOut": "{{checkIn.AddDays(1):yyyy-MM-dd}}",
              "guests": 1,
              "guestName": "Dorothy",
              "vehiclePreference": "SUV"
            }
            """;

        var accepted = await client.PostAsync(
            "/api/reservations",
            new StringContent(request, System.Text.Encoding.UTF8, "application/json"));
        var reservation = await accepted.Content.ReadFromJsonAsync<Reservation>();
        var invalidRequest = request
            .Replace($"\"{room.Id}\"", $"\"{hotel.Rooms[1].Id}\"", StringComparison.Ordinal)
            .Replace("\"SUV\"", "\"Truck\"", StringComparison.Ordinal);
        var rejected = await client.PostAsync(
            "/api/reservations",
            new StringContent(invalidRequest, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(VehiclePreference.SUV, reservation!.VehiclePreference);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("The request body is invalid.", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReservationRejectsNumericVehiclePreference()
    {
        var hotels = await client.GetFromJsonAsync<Hotel[]>("/api/hotels");
        var hotel = hotels![0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(120));
        var request = $$"""
            {
              "hotelId": "{{hotel.Id}}",
              "roomId": "{{room.Id}}",
              "checkIn": "{{checkIn:yyyy-MM-dd}}",
              "checkOut": "{{checkIn.AddDays(1):yyyy-MM-dd}}",
              "guests": 1,
              "guestName": "Mary",
              "vehiclePreference": 0
            }
            """;

        var response = await client.PostAsync(
            "/api/reservations",
            new StringContent(request, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The request body is invalid.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RecordedAgentEventAppearsInRecentHistory()
    {
        var request = new RecordAgentEventRequest(
            AgentEventKind.RouteDecided,
            "flow-42",
            "AB#958",
            "microsoft-router",
            "Selected the QA worker from the live worker menu.",
            "qa-agent",
            "shadow",
            0.94,
            27,
            "abc123");

        var created = await client.PostAsJsonAsync("/api/operations/events", request);
        var events = await client.GetFromJsonAsync<AgentEvent[]>("/api/operations/events");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(events!, item => item.CorrelationId == "flow-42" && item.Decision == "qa-agent");
    }

    [Fact]
    public async Task DeterministicPolicyRoutesToOnlyEligibleWorker()
    {
        var request = new RoutingRequest(
            "route-42",
            "AB#958",
            "quality-assurance",
            "browser-testing",
            "reversible",
            true,
            new Dictionary<string, string>
            {
                ["qa-agent"] = "Runs browser and API tests.",
                ["human_review"] = "Handles uncertain or disallowed routes."
            },
            "abc123");

        var response = await client.PostAsJsonAsync("/api/orchestration/route", request);
        var decision = await response.Content.ReadFromJsonAsync<RoutingDecision>();
        var events = await client.GetFromJsonAsync<AgentEvent[]>("/api/operations/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("qa-agent", decision!.EffectiveWorker);
        Assert.False(decision.Shadow);
        Assert.Contains(
            events!,
            item => item.CorrelationId == request.CorrelationId &&
                    item.Summary.StartsWith("policy:", StringComparison.Ordinal));
    }
}

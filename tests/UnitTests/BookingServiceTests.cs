using AgenticHotelBooking.Domain;
using AgenticHotelBooking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgenticHotelBooking.UnitTests;

public sealed class BookingServiceTests
{
    [Fact]
    public void VehiclePreferencesContainExactlyTheAcceptedCategories()
    {
        Assert.Equal(
            [VehiclePreference.Economy, VehiclePreference.Compact, VehiclePreference.SUV, VehiclePreference.Luxury],
            Enum.GetValues<VehiclePreference>());
    }

    [Fact]
    public async Task CreateReservationPreventsOverlappingBooking()
    {
        await using var context = CreateContext();
        var service = new EntityFrameworkHotelBookingService(context);
        var hotel = (await service.GetHotelsAsync(CancellationToken.None))[0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
        var checkOut = checkIn.AddDays(2);

        await service.CreateReservationAsync(
            new BookingRequest(hotel.Id, room.Id, checkIn, checkOut, 2, "Ada"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateReservationAsync(
                new BookingRequest(hotel.Id, room.Id, checkIn.AddDays(1), checkOut.AddDays(1), 2, "Grace"),
                CancellationToken.None));

        Assert.Contains("no longer available", exception.Message);
    }

    [Fact]
    public async Task SearchAvailabilityFiltersByCapacity()
    {
        await using var context = CreateContext();
        var service = new EntityFrameworkHotelBookingService(context);
        var hotel = (await service.GetHotelsAsync(CancellationToken.None))[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));

        var rooms = await service.SearchAvailabilityAsync(
            hotel.Id,
            checkIn,
            checkIn.AddDays(1),
            4,
            CancellationToken.None);

        Assert.Single(rooms);
        Assert.Equal("Family Loft", rooms[0].Name);
    }

    [Fact]
    public async Task CreateReservationCalculatesMultiNightTotal()
    {
        await using var context = CreateContext();
        var service = new EntityFrameworkHotelBookingService(context);
        var hotel = (await service.GetHotelsAsync(CancellationToken.None))[0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));

        var reservation = await service.CreateReservationAsync(
            new BookingRequest(hotel.Id, room.Id, checkIn, checkIn.AddDays(3), 2, "Ada"),
            CancellationToken.None);

        Assert.Equal(3, reservation.Nights);
        Assert.Equal(room.NightlyRate, reservation.NightlyRate);
        Assert.Equal(room.NightlyRate * 3, reservation.TotalStayPrice);
    }

    [Theory]
    [InlineData(VehiclePreference.Economy)]
    [InlineData(VehiclePreference.Compact)]
    [InlineData(VehiclePreference.SUV)]
    [InlineData(VehiclePreference.Luxury)]
    public async Task CreateReservationPersistsAcceptedVehiclePreference(VehiclePreference preference)
    {
        await using var context = CreateContext();
        var service = new EntityFrameworkHotelBookingService(context);
        var hotel = (await service.GetHotelsAsync(CancellationToken.None))[0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));

        var reservation = await service.CreateReservationAsync(
            new BookingRequest(hotel.Id, room.Id, checkIn, checkIn.AddDays(1), 1, "Ada", preference),
            CancellationToken.None);

        Assert.Equal(preference, reservation.VehiclePreference);
        Assert.Equal(preference, (await context.Reservations.SingleAsync()).VehiclePreference);
    }

    [Fact]
    public async Task CreateReservationSupportsNoVehiclePreference()
    {
        await using var context = CreateContext();
        var service = new EntityFrameworkHotelBookingService(context);
        var hotel = (await service.GetHotelsAsync(CancellationToken.None))[0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));

        var reservation = await service.CreateReservationAsync(
            new BookingRequest(hotel.Id, room.Id, checkIn, checkIn.AddDays(1), 1, "Ada"),
            CancellationToken.None);

        Assert.Null(reservation.VehiclePreference);
        Assert.Null((await context.Reservations.SingleAsync()).VehiclePreference);
    }

    [Fact]
    public async Task CreateReservationRejectsUnknownVehiclePreference()
    {
        await using var context = CreateContext();
        var service = new EntityFrameworkHotelBookingService(context);
        var hotel = (await service.GetHotelsAsync(CancellationToken.None))[0];
        var room = hotel.Rooms[0];
        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservationAsync(
                new BookingRequest(
                    hotel.Id,
                    room.Id,
                    checkIn,
                    checkIn.AddDays(1),
                    1,
                    "Ada",
                    (VehiclePreference)99),
                CancellationToken.None));

        Assert.Contains("must be Economy, Compact, SUV, Luxury, or omitted", exception.Message);
        Assert.Empty(context.Reservations);
    }

    private static HotelBookingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HotelBookingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new HotelBookingDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}

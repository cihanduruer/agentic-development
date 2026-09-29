using AgenticHotelBooking.Domain;
using AgenticHotelBooking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgenticHotelBooking.UnitTests;

public sealed class BookingServiceTests
{
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

namespace AgenticHotelBooking.Domain;

public sealed record Room(Guid Id, string Name, int Capacity, decimal NightlyRate);

public sealed record Hotel(
    Guid Id,
    string Name,
    string City,
    string Description,
    IReadOnlyList<Room> Rooms);

public sealed record Reservation(
    Guid Id,
    string Reference,
    Guid HotelId,
    Guid RoomId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Guests,
    string GuestName,
    DateTimeOffset CreatedAt);

public sealed record BookingRequest(
    Guid HotelId,
    Guid RoomId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Guests,
    string GuestName)
{
    public void Validate(DateOnly today)
    {
        if (CheckIn < today)
        {
            throw new ArgumentException("Check-in cannot be in the past.");
        }

        if (CheckOut <= CheckIn)
        {
            throw new ArgumentException("Check-out must be after check-in.");
        }

        if (Guests <= 0)
        {
            throw new ArgumentException("At least one guest is required.");
        }

        if (string.IsNullOrWhiteSpace(GuestName))
        {
            throw new ArgumentException("Guest name is required.");
        }
    }
}

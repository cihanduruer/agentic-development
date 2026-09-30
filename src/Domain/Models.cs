using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticHotelBooking.Domain;

[JsonConverter(typeof(VehiclePreferenceJsonConverter))]
public enum VehiclePreference
{
    Economy,
    Compact,
    SUV,
    Luxury
}

public sealed class VehiclePreferenceJsonConverter : JsonConverter<VehiclePreference>
{
    public override VehiclePreference Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                nameof(VehiclePreference.Economy) => VehiclePreference.Economy,
                nameof(VehiclePreference.Compact) => VehiclePreference.Compact,
                nameof(VehiclePreference.SUV) => VehiclePreference.SUV,
                nameof(VehiclePreference.Luxury) => VehiclePreference.Luxury,
                _ => throw new JsonException("Unknown vehicle preference.")
            }
            : throw new JsonException("Vehicle preference must be a named category.");

    public override void Write(
        Utf8JsonWriter writer,
        VehiclePreference value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            VehiclePreference.Economy => nameof(VehiclePreference.Economy),
            VehiclePreference.Compact => nameof(VehiclePreference.Compact),
            VehiclePreference.SUV => nameof(VehiclePreference.SUV),
            VehiclePreference.Luxury => nameof(VehiclePreference.Luxury),
            _ => throw new JsonException("Unknown vehicle preference.")
        });
}

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
    DateTimeOffset CreatedAt,
    decimal NightlyRate,
    VehiclePreference? VehiclePreference = null)
{
    public int Nights => CheckOut.DayNumber - CheckIn.DayNumber;

    public decimal TotalStayPrice => NightlyRate * Nights;
}

public sealed record BookingRequest(
    Guid HotelId,
    Guid RoomId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    int Guests,
    string GuestName,
    VehiclePreference? VehiclePreference = null)
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

        if (VehiclePreference is not null && !Enum.IsDefined(VehiclePreference.Value))
        {
            throw new ArgumentException("Vehicle preference must be Economy, Compact, SUV, Luxury, or omitted.");
        }
    }
}

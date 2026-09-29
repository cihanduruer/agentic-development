using System.Data;
using AgenticHotelBooking.Application;
using AgenticHotelBooking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticHotelBooking.Infrastructure;

public sealed class HotelBookingDbContext(DbContextOptions<HotelBookingDbContext> options)
    : DbContext(options)
{
    public DbSet<HotelEntity> Hotels => Set<HotelEntity>();
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<ReservationEntity> Reservations => Set<ReservationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HotelEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160);
            entity.Property(item => item.City).HasMaxLength(100);
            entity.Property(item => item.Description).HasMaxLength(600);
            entity.HasMany(item => item.Rooms).WithOne().HasForeignKey(item => item.HotelId);
        });

        modelBuilder.Entity<RoomEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(160);
            entity.Property(item => item.NightlyRate).HasPrecision(10, 2);
        });

        modelBuilder.Entity<ReservationEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Reference).IsUnique();
            entity.HasIndex(item => new { item.RoomId, item.CheckIn, item.CheckOut });
            entity.Property(item => item.Reference).HasMaxLength(20);
            entity.Property(item => item.GuestName).HasMaxLength(160);
        });

        modelBuilder.Entity<HotelEntity>().HasData(
            new HotelEntity
            {
                Id = Guid.Parse("3d41ab14-06c3-4c9a-bf29-61f4392f2227"),
                Name = "Canal House",
                City = "Amsterdam",
                Description = "A quiet canal-side stay close to the historic center."
            },
            new HotelEntity
            {
                Id = Guid.Parse("0e7b82cc-4ded-4707-82a4-1e80db408af9"),
                Name = "Harbor Light",
                City = "Rotterdam",
                Description = "Modern rooms overlooking Rotterdam's waterfront."
            });

        modelBuilder.Entity<RoomEntity>().HasData(
            new RoomEntity
            {
                Id = Guid.Parse("10af946b-1653-4ef7-b68c-47b436c34eb2"),
                HotelId = Guid.Parse("3d41ab14-06c3-4c9a-bf29-61f4392f2227"),
                Name = "Canal King",
                Capacity = 2,
                NightlyRate = 189m
            },
            new RoomEntity
            {
                Id = Guid.Parse("4135eb2b-04a0-4fa8-bb8d-fca4d16f96be"),
                HotelId = Guid.Parse("3d41ab14-06c3-4c9a-bf29-61f4392f2227"),
                Name = "Family Loft",
                Capacity = 4,
                NightlyRate = 269m
            },
            new RoomEntity
            {
                Id = Guid.Parse("163e83ce-a115-4b6d-a5c1-f794b2ced41e"),
                HotelId = Guid.Parse("0e7b82cc-4ded-4707-82a4-1e80db408af9"),
                Name = "Harbor Studio",
                Capacity = 2,
                NightlyRate = 149m
            },
            new RoomEntity
            {
                Id = Guid.Parse("bf8607bf-832f-4b05-bf7a-9ff0a1113fef"),
                HotelId = Guid.Parse("0e7b82cc-4ded-4707-82a4-1e80db408af9"),
                Name = "Panorama Suite",
                Capacity = 3,
                NightlyRate = 229m
            });
    }
}

public sealed class HotelEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string City { get; set; }
    public required string Description { get; set; }
    public List<RoomEntity> Rooms { get; set; } = [];
}

public sealed class RoomEntity
{
    public Guid Id { get; set; }
    public Guid HotelId { get; set; }
    public required string Name { get; set; }
    public int Capacity { get; set; }
    public decimal NightlyRate { get; set; }
}

public sealed class ReservationEntity
{
    public Guid Id { get; set; }
    public required string Reference { get; set; }
    public Guid HotelId { get; set; }
    public Guid RoomId { get; set; }
    public DateOnly CheckIn { get; set; }
    public DateOnly CheckOut { get; set; }
    public int Guests { get; set; }
    public required string GuestName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EntityFrameworkHotelBookingService(HotelBookingDbContext dbContext)
    : IHotelBookingService
{
    public async Task<IReadOnlyList<Hotel>> GetHotelsAsync(CancellationToken cancellationToken) =>
        await dbContext.Hotels
            .AsNoTracking()
            .Include(item => item.Rooms)
            .OrderBy(item => item.Name)
            .Select(item => new Hotel(
                item.Id,
                item.Name,
                item.City,
                item.Description,
                item.Rooms
                    .OrderBy(room => room.NightlyRate)
                    .Select(room => new Room(room.Id, room.Name, room.Capacity, room.NightlyRate))
                    .ToArray()))
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<Room>> SearchAvailabilityAsync(
        Guid hotelId,
        DateOnly checkIn,
        DateOnly checkOut,
        int guests,
        CancellationToken cancellationToken)
    {
        ValidateSearch(checkIn, checkOut, guests);
        if (!await dbContext.Hotels.AnyAsync(item => item.Id == hotelId, cancellationToken))
        {
            throw new KeyNotFoundException("Hotel was not found.");
        }

        return await dbContext.Rooms
            .AsNoTracking()
            .Where(room => room.HotelId == hotelId && room.Capacity >= guests)
            .Where(room => !dbContext.Reservations.Any(reservation =>
                reservation.RoomId == room.Id &&
                checkOut > reservation.CheckIn &&
                checkIn < reservation.CheckOut))
            .OrderBy(room => room.NightlyRate)
            .Select(room => new Room(room.Id, room.Name, room.Capacity, room.NightlyRate))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<Reservation> CreateReservationAsync(
        BookingRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate(DateOnly.FromDateTime(DateTime.UtcNow));
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => CreateReservationCoreAsync(request, cancellationToken));
    }

    private async Task<Reservation> CreateReservationCoreAsync(
        BookingRequest request,
        CancellationToken cancellationToken)
    {
        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;

        try
        {
            var room = await dbContext.Rooms.SingleOrDefaultAsync(
                item => item.Id == request.RoomId && item.HotelId == request.HotelId,
                cancellationToken)
                ?? throw new KeyNotFoundException("Room was not found.");

            if (room.Capacity < request.Guests)
            {
                throw new InvalidOperationException("The selected room cannot accommodate this party.");
            }

            var overlaps = await dbContext.Reservations.AnyAsync(reservation =>
                reservation.RoomId == request.RoomId &&
                request.CheckOut > reservation.CheckIn &&
                request.CheckIn < reservation.CheckOut,
                cancellationToken);
            if (overlaps)
            {
                throw new InvalidOperationException("The selected room is no longer available.");
            }

            var id = Guid.NewGuid();
            var entity = new ReservationEntity
            {
                Id = id,
                Reference = $"AHB-{id.ToString("N")[..8].ToUpperInvariant()}",
                HotelId = request.HotelId,
                RoomId = request.RoomId,
                CheckIn = request.CheckIn,
                CheckOut = request.CheckOut,
                Guests = request.Guests,
                GuestName = request.GuestName.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            dbContext.Reservations.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new Reservation(
                entity.Id,
                entity.Reference,
                entity.HotelId,
                entity.RoomId,
                entity.CheckIn,
                entity.CheckOut,
                entity.Guests,
                entity.GuestName,
                entity.CreatedAt);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static void ValidateSearch(DateOnly checkIn, DateOnly checkOut, int guests)
    {
        if (checkIn < DateOnly.FromDateTime(DateTime.UtcNow) || checkOut <= checkIn)
        {
            throw new ArgumentException("Choose valid future dates.");
        }

        if (guests <= 0)
        {
            throw new ArgumentException("At least one guest is required.");
        }
    }
}

public static class PersistenceRegistration
{
    public static IServiceCollection AddHotelBookingPersistence(
        this IServiceCollection services,
        string? connectionString)
    {
        services.AddDbContext<HotelBookingDbContext>(options =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseInMemoryDatabase("hotel-booking-development");
            }
            else
            {
                options.UseSqlServer(connectionString, sql =>
                    sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
            }
        });
        services.AddScoped<IHotelBookingService, EntityFrameworkHotelBookingService>();
        return services;
    }
}

public sealed class HotelBookingDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<HotelBookingDbContext>
{
    public HotelBookingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HotelBookingDbContext>()
            .UseSqlServer(@"Server=(localdb)\MSSQLLocalDB;Database=HotelBookingDesign;Trusted_Connection=True")
            .Options;
        return new HotelBookingDbContext(options);
    }
}

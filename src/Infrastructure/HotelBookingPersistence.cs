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
    public DbSet<AgentEventEntity> AgentEvents => Set<AgentEventEntity>();

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

        modelBuilder.Entity<AgentEventEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.Timestamp, item.Id });
            entity.Property(item => item.CorrelationId).HasMaxLength(100);
            entity.Property(item => item.WorkItemId).HasMaxLength(40);
            entity.Property(item => item.Agent).HasMaxLength(100);
            entity.Property(item => item.Summary).HasMaxLength(1000);
            entity.Property(item => item.Decision).HasMaxLength(200);
            entity.Property(item => item.Outcome).HasMaxLength(200);
            entity.Property(item => item.KnowledgeRevision).HasMaxLength(64);
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

public sealed class AgentEventEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public AgentEventKind Kind { get; set; }
    public required string CorrelationId { get; set; }
    public required string WorkItemId { get; set; }
    public required string Agent { get; set; }
    public required string Summary { get; set; }
    public required string Decision { get; set; }
    public required string Outcome { get; set; }
    public double? Confidence { get; set; }
    public long DurationMilliseconds { get; set; }
    public required string KnowledgeRevision { get; set; }
}

public sealed record AgentEventStoreOptions(
    int RetentionDays = 30,
    int MaxRecords = 2_000,
    int MaxQueryLimit = 500)
{
    public AgentEventStoreOptions Validate()
    {
        if (RetentionDays is < 1 or > 365 ||
            MaxRecords is < 1 or > 1_000_000 ||
            MaxQueryLimit is < 1 or > 10_000)
        {
            throw new InvalidOperationException("Agent event retention configuration is outside supported bounds.");
        }

        return this;
    }
}

public sealed class EntityFrameworkAgentEventStore(
    HotelBookingDbContext dbContext,
    AgentEventStoreOptions options,
    TimeProvider timeProvider)
    : IAgentEventStore
{
    private readonly AgentEventStoreOptions options = options.Validate();

    public async Task<IReadOnlyList<AgentEvent>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, options.MaxQueryLimit);
        return await dbContext.AgentEvents
            .AsNoTracking()
            .OrderByDescending(item => item.Timestamp)
            .ThenByDescending(item => item.Id)
            .Take(boundedLimit)
            .Select(item => ToContract(item))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<AgentEvent> RecordAsync(
        RecordAgentEventRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var entity = new AgentEventEntity
        {
            Id = Guid.NewGuid(),
            Timestamp = timeProvider.GetUtcNow(),
            Kind = request.Kind,
            CorrelationId = request.CorrelationId.Trim(),
            WorkItemId = request.WorkItemId.Trim(),
            Agent = request.Agent.Trim(),
            Summary = request.Summary.Trim(),
            Decision = request.Decision.Trim(),
            Outcome = request.Outcome.Trim(),
            Confidence = request.Confidence,
            DurationMilliseconds = Math.Max(0, request.DurationMilliseconds),
            KnowledgeRevision = request.KnowledgeRevision.Trim()
        };

        dbContext.AgentEvents.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        await ApplyRetentionAsync(cancellationToken);
        return ToContract(entity);
    }

    private async Task ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.RetentionDays);
        var expired = await dbContext.AgentEvents
            .Where(item => item.Timestamp < cutoff)
            .ToArrayAsync(cancellationToken);
        dbContext.AgentEvents.RemoveRange(expired);

        var overflowIds = await dbContext.AgentEvents
            .OrderByDescending(item => item.Timestamp)
            .ThenByDescending(item => item.Id)
            .Skip(options.MaxRecords)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        if (overflowIds.Length > 0)
        {
            var overflow = await dbContext.AgentEvents
                .Where(item => overflowIds.Contains(item.Id))
                .ToArrayAsync(cancellationToken);
            dbContext.AgentEvents.RemoveRange(overflow);
        }

        if (expired.Length > 0 || overflowIds.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static AgentEvent ToContract(AgentEventEntity item) =>
        new(
            item.Id,
            item.Timestamp,
            item.Kind,
            item.CorrelationId,
            item.WorkItemId,
            item.Agent,
            item.Summary,
            item.Decision,
            item.Outcome,
            item.Confidence,
            item.DurationMilliseconds,
            item.KnowledgeRevision);

    private static void Validate(RecordAgentEventRequest request)
    {
        if (request.CorrelationId is null ||
            request.WorkItemId is null ||
            request.Agent is null ||
            request.Summary is null ||
            request.Decision is null ||
            request.Outcome is null ||
            request.KnowledgeRevision is null)
        {
            throw new ArgumentException("Agent event text fields cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(request.CorrelationId) ||
            string.IsNullOrWhiteSpace(request.Agent) ||
            string.IsNullOrWhiteSpace(request.Summary))
        {
            throw new ArgumentException("Correlation ID, agent, and summary are required.");
        }

        if (request.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Confidence must be between 0 and 1.");
        }

        ValidateLength(request.CorrelationId, 100, nameof(request.CorrelationId));
        ValidateLength(request.WorkItemId, 40, nameof(request.WorkItemId));
        ValidateLength(request.Agent, 100, nameof(request.Agent));
        ValidateLength(request.Summary, 1000, nameof(request.Summary));
        ValidateLength(request.Decision, 200, nameof(request.Decision));
        ValidateLength(request.Outcome, 200, nameof(request.Outcome));
        ValidateLength(request.KnowledgeRevision, 64, nameof(request.KnowledgeRevision));
    }

    private static void ValidateLength(string value, int maximumLength, string field)
    {
        if (value.Trim().Length > maximumLength)
        {
            throw new ArgumentException($"{field} cannot exceed {maximumLength} characters.", field);
        }
    }
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
                entity.CreatedAt,
                room.NightlyRate);
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
        string? connectionString,
        AgentEventStoreOptions agentEventOptions)
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
        services.AddScoped<IAgentEventStore, EntityFrameworkAgentEventStore>();
        services.AddSingleton(agentEventOptions.Validate());
        services.AddSingleton(TimeProvider.System);
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

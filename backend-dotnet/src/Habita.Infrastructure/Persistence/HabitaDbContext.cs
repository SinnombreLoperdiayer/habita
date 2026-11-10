using System.Text.Json;
using System.Text.Json.Serialization;
using Habita.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Habita.Infrastructure.Persistence;

public sealed class HabitaDbContext(DbContextOptions<HabitaDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Recording>(e =>
        {
            e.ToTable("Recordings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(32).IsUnicode(false);
            e.Property(r => r.Title).HasMaxLength(300);
            e.Property(r => r.Community).HasMaxLength(300);
            e.Property(r => r.ConsentBy).HasMaxLength(200);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(r => r.Error).HasMaxLength(Recording.MaxErrorLength);
            AsJson(e.Property(r => r.Markers));
            AsJson(e.Property(r => r.Context));
            e.Ignore(r => r.IsProcessing);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.MeetingId);
        });

        modelBuilder.Entity<Chunk>(e =>
        {
            e.ToTable("Chunks");
            e.HasKey(c => new { c.RecordingId, c.Seq });
            e.Property(c => c.RecordingId).HasMaxLength(32).IsUnicode(false);
            e.Property(c => c.Mime).HasMaxLength(100).IsUnicode(false);
            e.Property(c => c.AudioKey).HasMaxLength(400);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
            e.Property(c => c.Error).HasMaxLength(Chunk.MaxErrorLength);
            e.Ignore(c => c.MidMs);
            e.HasOne<Recording>().WithMany().HasForeignKey(c => c.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>Guarda el valor como JSON en nvarchar(max); consultable desde SSMS con JSON_VALUE.</summary>
    private static void AsJson<T>(PropertyBuilder<T> property) where T : class =>
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                s => JsonSerializer.Deserialize<T>(s, Json)!,
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!))
            .IsRequired();
}

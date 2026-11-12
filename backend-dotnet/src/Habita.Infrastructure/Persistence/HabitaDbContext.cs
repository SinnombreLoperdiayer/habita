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
    public DbSet<Community> Communities => Set<Community>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Membership> Memberships => Set<Membership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Community>(e =>
        {
            e.ToTable("Communities");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(32).IsUnicode(false);
            e.Property(c => c.Name).HasMaxLength(Community.MaxNameLength);
        });

        modelBuilder.Entity<Person>(e =>
        {
            e.ToTable("People");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(32).IsUnicode(false);
            e.Property(p => p.ExternalId).HasMaxLength(Person.MaxExternalIdLength).IsUnicode(false);
            e.Property(p => p.Email).HasMaxLength(Person.MaxEmailLength);
            e.Property(p => p.DisplayName).HasMaxLength(Person.MaxNameLength);
            e.Ignore(p => p.IsLinked);
            // Índices únicos sobre columnas que admiten NULL: SQL Server los filtra con "IS NOT NULL".
            e.HasIndex(p => p.ExternalId).IsUnique();
            e.HasIndex(p => p.Email).IsUnique();
        });

        modelBuilder.Entity<Membership>(e =>
        {
            e.ToTable("Memberships");
            e.HasKey(m => new { m.CommunityId, m.PersonId, m.Role });
            e.Property(m => m.CommunityId).HasMaxLength(32).IsUnicode(false);
            e.Property(m => m.PersonId).HasMaxLength(32).IsUnicode(false);
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
            e.HasOne<Community>().WithMany().HasForeignKey(m => m.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Person>().WithMany().HasForeignKey(m => m.PersonId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.PersonId);
        });

        modelBuilder.Entity<Recording>(e =>
        {
            e.ToTable("Recordings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(32).IsUnicode(false);
            // Sin clave foránea: las grabaciones anteriores al control de acceso quedan con CommunityId vacío.
            e.Property(r => r.CommunityId).HasMaxLength(32).IsUnicode(false);
            e.HasIndex(r => r.CommunityId);
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

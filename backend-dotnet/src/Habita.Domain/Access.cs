using System.Net.Mail;

namespace Habita.Domain;

/// <summary>Roles permanentes de una persona dentro de una comunidad. Una persona puede tener varios.</summary>
public enum CommunityRole { Administrator, RegistrationStaff, Council, FiscalAuditor, Owner }

/// <summary>Copropiedad (conjunto o edificio) que usa Habita. Cada comunidad es un espacio de datos separado.</summary>
public sealed class Community
{
    public const int MaxNameLength = 200;

    private Community() { } // EF Core

    public string Id { get; private set; } = "";
    public string Name { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static Community Create(string name, DateTimeOffset now)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new DomainException("El nombre de la comunidad es obligatorio.");
        if (trimmed.Length > MaxNameLength) throw new DomainException($"El nombre de la comunidad supera {MaxNameLength} caracteres.");
        return new Community { Id = Ids.New(), Name = trimmed, CreatedAt = now };
    }
}

/// <summary>
/// Persona con acceso a Habita. Se crea al invitarla por correo y queda vinculada a su cuenta de
/// Entra External ID (ExternalId) la primera vez que inicia sesión con ese correo.
/// </summary>
public sealed class Person
{
    public const int MaxEmailLength = 254;
    public const int MaxNameLength = 200;
    public const int MaxExternalIdLength = 64;

    private Person() { } // EF Core

    public string Id { get; private set; } = "";
    public string? ExternalId { get; private set; }
    public string? Email { get; private set; }
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsLinked => ExternalId is not null;

    public static Person Invite(string email, string? displayName, DateTimeOffset now)
    {
        var normalized = NormalizeEmail(email) ?? throw new DomainException("El correo no es válido.");
        return new Person { Id = Ids.New(), Email = normalized, DisplayName = CleanName(displayName) ?? normalized, CreatedAt = now };
    }

    /// <summary>Correo en minúsculas y sin espacios, o null si no es una dirección válida.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxEmailLength) return null;
        return MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed && address.DisplayName.Length == 0
            ? trimmed.ToLowerInvariant()
            : null;
    }

    public static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }
}

/// <summary>Rol de una persona en una comunidad.</summary>
public sealed class Membership
{
    private Membership() { } // EF Core

    public Membership(string communityId, string personId, CommunityRole role, DateTimeOffset now)
    {
        CommunityId = communityId;
        PersonId = personId;
        Role = role;
        CreatedAt = now;
    }

    public string CommunityId { get; private set; } = "";
    public string PersonId { get; private set; } = "";
    public CommunityRole Role { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public static class Ids
{
    /// <summary>Identificador aleatorio de 12 caracteres hexadecimales.</summary>
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}

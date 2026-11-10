namespace Habita.Domain;

public sealed record AgendaItem(string Title, string? Time = null, string? Detail = null);

public sealed record Attendance(int Present, int Total, double Percent);

public sealed record PollResult(string Title, int Yes, int No, int Abstain);

/// <summary>Datos de la reunión que el cliente envía al iniciar y al finalizar la grabación.</summary>
public sealed record MeetingContext
{
    public static readonly MeetingContext Empty = new();

    public string? Date { get; init; }
    public string? Time { get; init; }
    public string? Venue { get; init; }
    public IReadOnlyList<AgendaItem>? Agenda { get; init; }
    public Attendance? Attendance { get; init; }
    public IReadOnlyList<PollResult>? Polls { get; init; }

    /// <summary>Los campos enviados reemplazan a los guardados; los omitidos se conservan.</summary>
    public MeetingContext MergeWith(MeetingContext? update) => update is null ? this : new MeetingContext
    {
        Date = update.Date ?? Date,
        Time = update.Time ?? Time,
        Venue = update.Venue ?? Venue,
        Agenda = update.Agenda ?? Agenda,
        Attendance = update.Attendance ?? Attendance,
        Polls = update.Polls ?? Polls,
    };
}

using Habita.Application;
using Habita.Domain;

namespace Habita.Tests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 18, 0, 0, TimeSpan.FromHours(-5));

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(61_500, "00:01:01")]
    [InlineData(3_723_000, "01:02:03")]
    [InlineData(-5, "00:00:00")]
    public void Formats_timestamps(long ms, string expected) => Assert.Equal(expected, Timestamp.Format(ms));

    [Fact]
    public void Finish_sorts_markers_and_merges_context()
    {
        var rec = Recording.Start(1, "Asamblea", "Los Olivos", "  Presidente ", new MeetingContext { Date = "2026-11-10", Venue = "Salón" }, Now);
        rec.Finish([new Marker(5000, MarkerKind.Agenda, "B"), new Marker(0, MarkerKind.Agenda, "A")], new MeetingContext { Venue = "Terraza" }, Now);

        Assert.Equal("Presidente", rec.ConsentBy);
        Assert.Equal(["A", "B"], rec.Markers.Select(m => m.Label));
        Assert.Equal("2026-11-10", rec.Context.Date);
        Assert.Equal("Terraza", rec.Context.Venue);
        Assert.Equal(RecordingStatus.Queued, rec.Status);
    }

    [Fact]
    public void Audio_cannot_be_deleted_while_processing()
    {
        var rec = Recording.Start(1, "t", "c", "p", null, Now);
        rec.Finish([], null, Now);
        Assert.Throws<DomainException>(rec.MarkAudioDeleted);
    }

    [Fact]
    public void Acta_uses_colombian_number_format_and_escapes_table_pipes()
    {
        var rec = Recording.Start(1, "Asamblea", "Los Olivos", "p", new MeetingContext
        {
            Attendance = new Attendance(1, 3, 33.3),
            Polls = [new PollResult("Obra | fase 2", 2, 1, 0)],
        }, Now);
        var acta = ActaDrafting.RenderActa(rec, [("Punto", "Cuerpo")]);

        Assert.Contains("(33,3 %)", acta, StringComparison.Ordinal);
        Assert.Contains("| Obra \\| fase 2 | 2 | 1 | 0 |", acta, StringComparison.Ordinal);
        Assert.Contains("### 1. Punto\n\nCuerpo", acta, StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_without_speech_are_flagged_and_events_are_kept()
    {
        var rec = Recording.Start(1, "t", "c", "p", null, Now);
        rec.Finish([new Marker(10_000, MarkerKind.Agenda, "Uno"), new Marker(20_000, MarkerKind.Vote, "Votación")], null, Now);
        var sections = ActaDrafting.Sections(rec, []);

        Assert.Equal(["Instalación y asuntos previos", "Uno"], sections.Select(s => s.Title));
        Assert.False(sections[1].HasSpeech);
        Assert.Equal("[00:00:20] [EVENTO · votación] Votación", sections[1].Lines.Single().ToString());
    }
}

using System.Globalization;
using System.Text;
using Habita.Domain;

namespace Habita.Application;

/// <summary>Línea de la transcripción con el instante en que empieza su segmento.</summary>
public sealed record TranscriptLine(long StartMs, long MidMs, string Text);

/// <summary>Punto del acta con sus líneas ya etiquetadas con [hh:mm:ss].</summary>
public sealed record ActaSection(string Title, IReadOnlyList<SectionLine> Lines)
{
    public bool HasSpeech => Lines.Any(l => !l.IsEvent);
}

public sealed record SectionLine(long TMs, string Text, bool IsEvent)
{
    public override string ToString() => $"[{Timestamp.Format(TMs)}] {Text}";
}

/// <summary>Reglas puras para armar el acta: división por puntos, prompts y plantilla determinista.</summary>
public static class ActaDrafting
{
    public const string NoSpeech = "Sin audio registrado para este punto.";
    public const string UntranscribedAudio = "[AUDIO NO TRANSCRITO]";

    public const string SectionSystemPrompt =
        "Eres el secretario de una asamblea de copropietarios en Colombia (propiedad horizontal, Ley 675 de 2001). " +
        "Redactas el desarrollo de UN punto del orden del día para el acta, en español formal, tercera persona y pretérito.\n" +
        "Reglas estrictas:\n" +
        "- Usa únicamente lo que consta en la transcripción. No inventes nombres, cifras, votos, decisiones ni compromisos.\n" +
        "- La transcripción es automática y puede tener errores. Si un dato es ambiguo o dudoso (nombres, números, unidades), escríbelo y márcalo con [POR CONFIRMAR].\n" +
        "- Al final de cada afirmación relevante cita la marca de tiempo entre corchetes, por ejemplo [00:12:30].\n" +
        "- Si hubo proposiciones o decisiones, destácalas en viñetas bajo 'Proposiciones y decisiones'. Los resultados de votación oficiales se añaden aparte; no los calcules.\n" +
        "- Si el texto no contiene contenido sustancial, responde exactamente: Sin contenido sustancial registrado en el audio.\n" +
        "- Responde solo con el texto del punto, sin título.";

    private static readonly CultureInfo EsCo = CultureInfo.GetCultureInfo("es-CO");

    public static string TranscriptionPrompt(Recording rec)
    {
        var topics = string.Join(", ", (rec.Context.Agenda ?? []).Select(a => a.Title));
        return $"Asamblea de copropietarios en Colombia, {rec.Community}. Temas: {topics}. " +
               "Vocabulario: propiedad horizontal, coeficiente, quórum, torre, apartamento, administrador, consejo de administración, proposición.";
    }

    public static IReadOnlyList<TranscriptLine> TranscriptLines(IEnumerable<Chunk> chunks)
    {
        var lines = new List<TranscriptLine>();
        foreach (var c in chunks.OrderBy(c => c.StartMs))
        {
            if (c.Status == ChunkStatus.Transcribed && !string.IsNullOrWhiteSpace(c.Text))
                lines.Add(new TranscriptLine(c.StartMs, c.MidMs, c.Text.Trim()));
            else if (c.Status == ChunkStatus.Failed)
                lines.Add(new TranscriptLine(c.StartMs, c.MidMs, UntranscribedAudio));
        }
        return lines;
    }

    /// <summary>
    /// Divide la transcripción en puntos del orden del día según los marcadores.
    /// Cada segmento va al punto en el que cae su punto medio.
    /// </summary>
    public static IReadOnlyList<ActaSection> Sections(Recording rec, IEnumerable<Chunk> chunks)
    {
        var lines = TranscriptLines(chunks);
        var agenda = rec.Markers.Where(m => m.Kind == MarkerKind.Agenda).ToList();
        var events = rec.Markers.Where(m => m.Kind != MarkerKind.Agenda).ToList();

        var bounds = new List<(string Title, long From, long? To)>();
        if (agenda.Count == 0)
        {
            bounds.Add(("Desarrollo de la sesión", 0, null));
        }
        else
        {
            if (agenda[0].TMs > 0) bounds.Add(("Instalación y asuntos previos", 0, agenda[0].TMs));
            for (var i = 0; i < agenda.Count; i++)
                bounds.Add((agenda[i].Label, agenda[i].TMs, i + 1 < agenda.Count ? agenda[i + 1].TMs : null));
        }

        static bool Within(long t, long from, long? to) => from <= t && (to is null || t < to);

        return [.. bounds.Select(b =>
        {
            var items = lines.Where(l => Within(l.MidMs, b.From, b.To)).Select(l => new SectionLine(l.StartMs, l.Text, false))
                .Concat(events.Where(m => Within(m.TMs, b.From, b.To))
                    .Select(m => new SectionLine(m.TMs, $"[EVENTO · {(m.Kind == MarkerKind.Vote ? "votación" : "nota")}] {m.Label}", true)))
                .OrderBy(x => x.TMs)
                .ToList();
            return new ActaSection(b.Title, items);
        })];
    }

    public static string SectionUserPrompt(ActaSection section) =>
        $"Punto del orden del día: {section.Title}\n\nTranscripción (con marcas de tiempo):\n" + string.Join("\n", section.Lines);

    /// <summary>Encabezado, quórum, votaciones y firmas se generan sin IA; solo el cuerpo de cada punto viene del modelo.</summary>
    public static string RenderActa(Recording rec, IReadOnlyList<(string Title, string Body)> sections)
    {
        var ctx = rec.Context;
        var att = ctx.Attendance;
        var sb = new StringBuilder();
        sb.Append("# ACTA DE ASAMBLEA — BORRADOR\n\n")
          .Append(EsCo, $"**{rec.Community}**  \n{rec.Title}  \n{ctx.Date} · {ctx.Time} h · {ctx.Venue}\n\n");

        sb.Append("## Verificación del quórum\n\n")
          .Append(att is null
              ? "Asistieron — de — unidades privadas (— %). "
              : string.Create(EsCo, $"Asistieron {att.Present} de {att.Total} unidades privadas ({att.Percent} %). "))
          .Append("[POR CONFIRMAR: el quórum legal se determina por coeficientes de copropiedad.]\n\n");

        sb.Append("## Desarrollo de la sesión\n\n");
        for (var i = 0; i < sections.Count; i++)
            sb.Append(EsCo, $"### {i + 1}. {sections[i].Title}\n\n{sections[i].Body}\n\n");

        if (ctx.Polls is { Count: > 0 } polls)
        {
            sb.Append("## Resultados de votaciones registrados en el sistema\n\n")
              .Append("| Proposición | A favor | En contra | Abstención |\n|---|---|---|---|\n");
            foreach (var p in polls)
                sb.Append(EsCo, $"| {p.Title.Replace("|", "\\|", StringComparison.Ordinal)} | {p.Yes} | {p.No} | {p.Abstain} |\n");
            sb.Append('\n');
        }

        sb.Append("## Cierre y firmas\n\n_______________________  \nPresidente de la asamblea\n\n_______________________  \nSecretario\n\n")
          .Append("> Borrador generado automáticamente a partir de una grabación. Debe ser revisado, corregido y aprobado por las personas designadas por la asamblea antes de ser considerado acta oficial.");
        return sb.ToString();
    }
}

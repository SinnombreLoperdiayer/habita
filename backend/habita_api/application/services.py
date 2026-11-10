"""Casos de uso: orquestan dominio y puertos. No conocen OpenAI, SQLite ni FastAPI."""
from __future__ import annotations

import asyncio
import logging
import time
import uuid

from habita_api.application.ports import AudioStore, JobQueue, RecordingRepository, TextGenerator, Transcriber
from habita_api.domain.models import Chunk, ChunkStatus, Marker, Recording, RecordingStatus, fmt_ts

log = logging.getLogger("habita.recordings")

MAX_CHUNK_BYTES = 24 * 1024 * 1024  # límite de archivo del servicio de transcripción (25 MB)
EXT_BY_MIME = {"audio/webm": "webm", "audio/mp4": "mp4", "audio/ogg": "ogg", "audio/wav": "wav", "audio/mpeg": "mp3", "audio/x-m4a": "m4a"}

SECTION_SYSTEM = (
    "Eres el secretario de una asamblea de copropietarios en Colombia (propiedad horizontal, Ley 675 de 2001). "
    "Redactas el desarrollo de UN punto del orden del día para el acta, en español formal, tercera persona y pretérito.\n"
    "Reglas estrictas:\n"
    "- Usa únicamente lo que consta en la transcripción. No inventes nombres, cifras, votos, decisiones ni compromisos.\n"
    "- La transcripción es automática y puede tener errores. Si un dato es ambiguo o dudoso (nombres, números, unidades), escríbelo y márcalo con [POR CONFIRMAR].\n"
    "- Al final de cada afirmación relevante cita la marca de tiempo entre corchetes, por ejemplo [00:12:30].\n"
    "- Si hubo proposiciones o decisiones, destácalas en viñetas bajo 'Proposiciones y decisiones'. Los resultados de votación oficiales se añaden aparte; no los calcules.\n"
    "- Si el texto no contiene contenido sustancial, responde exactamente: Sin contenido sustancial registrado en el audio.\n"
    "- Responde solo con el texto del punto, sin título."
)


class NotFound(Exception):
    pass


class Invalid(Exception):
    pass


def transcript_lines(chunks: list[Chunk]) -> list[tuple[int, str]]:
    out = []
    for c in sorted(chunks, key=lambda c: c.start_ms):
        if c.status == ChunkStatus.TRANSCRIBED and c.text.strip():
            out.append((c.start_ms, c.text.strip()))
        elif c.status == ChunkStatus.FAILED:
            out.append((c.start_ms, "[AUDIO NO TRANSCRITO]"))
    return out


class RecordingService:
    def __init__(self, repo: RecordingRepository, audio: AudioStore, transcriber: Transcriber, generator: TextGenerator, queue: JobQueue):
        self.repo, self.audio, self.transcriber, self.generator, self.queue = repo, audio, transcriber, generator, queue

    # ---- captura -----------------------------------------------------
    def start(self, meeting_id: int, title: str, community: str, consent_by: str, context: dict) -> Recording:
        if not consent_by.strip():
            raise Invalid("Se requiere registrar quién autoriza la grabación (consentimiento).")
        now = time.time()
        rec = Recording(id=uuid.uuid4().hex[:12], meeting_id=meeting_id, title=title, community=community, consent_by=consent_by.strip(), consent_at=now, created_at=now, context=context)
        self.repo.add(rec)
        return rec

    def upload_chunk(self, rec_id: str, seq: int, start_ms: int, duration_ms: int, mime: str, data: bytes) -> Chunk:
        rec = self._get(rec_id)
        if rec.audio_deleted:
            raise Invalid("El audio de esta grabación fue eliminado.")
        if not data:
            raise Invalid("Fragmento vacío.")
        if len(data) > MAX_CHUNK_BYTES:
            raise Invalid("Fragmento demasiado grande.")
        base = mime.split(";")[0].strip().lower()
        path = self.audio.save(rec_id, seq, data, EXT_BY_MIME.get(base, "webm"))
        chunk = Chunk(recording_id=rec_id, seq=seq, start_ms=start_ms, duration_ms=duration_ms, mime=base, path=path)
        self.repo.upsert_chunk(chunk)
        if rec.status in (RecordingStatus.READY, RecordingStatus.FAILED):  # fragmento tardío: reprocesar
            rec.status = RecordingStatus.QUEUED
            self.repo.save(rec)
            self.queue.enqueue(rec_id)
        return chunk

    def finish(self, rec_id: str, markers: list[Marker], context: dict | None) -> Recording:
        rec = self._get(rec_id)
        rec.markers = sorted(markers, key=lambda m: m.t_ms)
        if context:
            rec.context = {**rec.context, **context}
        rec.finished_at = time.time()
        rec.status = RecordingStatus.QUEUED
        self.repo.save(rec)
        self.queue.enqueue(rec_id)
        return rec

    # ---- procesamiento asíncrono (worker) ----------------------------
    async def process(self, rec_id: str) -> None:
        rec = self._get(rec_id)
        try:
            rec.status, rec.error = RecordingStatus.TRANSCRIBING, ""
            self.repo.save(rec)
            await self._transcribe_pending(rec)
            chunks = self.repo.chunks(rec_id)
            if chunks and all(c.status == ChunkStatus.FAILED for c in chunks):
                raise RuntimeError("No se pudo transcribir ningún fragmento: " + (chunks[0].error or "error desconocido"))
            rec.status = RecordingStatus.DRAFTING
            self.repo.save(rec)
            rec.draft = await self.build_draft(rec, chunks)
            rec.status = RecordingStatus.READY
        except Exception as exc:  # noqa: BLE001
            log.exception("Fallo procesando %s", rec_id)
            rec.status, rec.error = RecordingStatus.FAILED, str(exc)[:500]
        self.repo.save(rec)

    async def _transcribe_pending(self, rec: Recording) -> None:
        pending = [c for c in self.repo.chunks(rec.id) if c.status != ChunkStatus.TRANSCRIBED]
        sem = asyncio.Semaphore(3)
        agenda = ", ".join(a.get("title", "") for a in rec.context.get("agenda", []))
        prompt = f"Asamblea de copropietarios en Colombia, {rec.community}. Temas: {agenda}. Vocabulario: propiedad horizontal, coeficiente, quórum, torre, apartamento, administrador, consejo de administración, proposición."

        async def one(c: Chunk) -> None:
            async with sem:
                last = ""
                for attempt in range(3):
                    try:
                        data = self.audio.read(c.path)
                        c.text = await self.transcriber.transcribe(data, f"{rec.id}-{c.seq}.{c.path.rsplit('.', 1)[-1]}", c.mime, prompt)
                        c.status, c.error = ChunkStatus.TRANSCRIBED, ""
                        break
                    except Exception as exc:  # noqa: BLE001
                        last = str(exc)[:300]
                        await asyncio.sleep(2**attempt)
                else:
                    c.status, c.error = ChunkStatus.FAILED, last
                self.repo.upsert_chunk(c)

        await asyncio.gather(*(one(c) for c in pending))

    # ---- borrador del acta -------------------------------------------
    def sections(self, rec: Recording, chunks: list[Chunk]) -> list[tuple[str, list[str]]]:
        lines = transcript_lines(chunks)
        mid = {c.start_ms: c.mid_ms for c in chunks}
        agenda = [m for m in rec.markers if m.kind == "agenda"]
        events = [m for m in rec.markers if m.kind != "agenda"]
        bounds: list[tuple[str, int, int | None]] = []
        if not agenda:
            bounds.append(("Desarrollo de la sesión", 0, None))
        else:
            if agenda[0].t_ms > 0:
                bounds.append(("Instalación y asuntos previos", 0, agenda[0].t_ms))
            for i, m in enumerate(agenda):
                bounds.append((m.label, m.t_ms, agenda[i + 1].t_ms if i + 1 < len(agenda) else None))
        out = []
        for title, a, b in bounds:
            items: list[tuple[int, str]] = []
            for t, text in lines:
                if t >= 0 and a <= mid.get(t, t) and (b is None or mid.get(t, t) < b):
                    items.append((t, text))
            for m in events:
                if a <= m.t_ms and (b is None or m.t_ms < b):
                    items.append((m.t_ms, f"[EVENTO · {'votación' if m.kind == 'vote' else 'nota'}] {m.label}"))
            items.sort(key=lambda x: x[0])
            out.append((title, [f"[{fmt_ts(t)}] {text}" for t, text in items]))
        return out

    async def build_draft(self, rec: Recording, chunks: list[Chunk]) -> str:
        sem = asyncio.Semaphore(4)

        async def write(title: str, lines: list[str]) -> str:
            if not any(not ln.split("] ", 1)[-1].startswith("[EVENTO") for ln in lines):
                return "Sin audio registrado para este punto."
            user = f"Punto del orden del día: {title}\n\nTranscripción (con marcas de tiempo):\n" + "\n".join(lines)
            async with sem:
                return (await self.generator.generate(SECTION_SYSTEM, user)).strip()

        secs = self.sections(rec, chunks)
        bodies = await asyncio.gather(*(write(t, ls) for t, ls in secs))
        return render_acta(rec, [(t, b) for (t, _), b in zip(secs, bodies, strict=True)])

    def regenerate(self, rec_id: str) -> Recording:
        rec = self._get(rec_id)
        rec.status = RecordingStatus.QUEUED
        self.repo.save(rec)
        self.queue.enqueue(rec_id)
        return rec

    def save_draft(self, rec_id: str, draft: str) -> Recording:
        rec = self._get(rec_id)
        rec.draft = draft
        self.repo.save(rec)
        return rec

    def delete_audio(self, rec_id: str) -> Recording:
        rec = self._get(rec_id)
        if rec.status in (RecordingStatus.QUEUED, RecordingStatus.TRANSCRIBING, RecordingStatus.DRAFTING):
            raise Invalid("Espera a que termine el procesamiento para eliminar el audio.")
        self.audio.delete_recording(rec_id)
        rec.audio_deleted = True
        self.repo.save(rec)
        return rec

    def view(self, rec_id: str) -> tuple[Recording, list[Chunk]]:
        return self._get(rec_id), self.repo.chunks(rec_id)

    def _get(self, rec_id: str) -> Recording:
        rec = self.repo.get(rec_id)
        if not rec:
            raise NotFound(rec_id)
        return rec


def render_acta(rec: Recording, sections: list[tuple[str, str]]) -> str:
    ctx = rec.context
    att = ctx.get("attendance") or {}
    out = [
        f"# ACTA DE ASAMBLEA — BORRADOR\n\n**{rec.community}**  \n{rec.title}  \n{ctx.get('date', '')} · {ctx.get('time', '')} h · {ctx.get('venue', '')}\n",
        "## Verificación del quórum\n",
        f"Asistieron {att.get('present', '—')} de {att.get('total', '—')} unidades privadas ({att.get('percent', '—')} %). "
        "[POR CONFIRMAR: el quórum legal se determina por coeficientes de copropiedad.]\n",
        "## Desarrollo de la sesión\n",
    ]
    for i, (title, body) in enumerate(sections, 1):
        out.append(f"### {i}. {title}\n\n{body}\n")
    polls = ctx.get("polls") or []
    if polls:
        out.append("## Resultados de votaciones registrados en el sistema\n")
        out.append("| Proposición | A favor | En contra | Abstención |\n|---|---|---|---|")
        out += [f"| {p.get('title', '')} | {p.get('yes', 0)} | {p.get('no', 0)} | {p.get('abstain', 0)} |" for p in polls]
        out.append("")
    out.append(
        "## Cierre y firmas\n\n_______________________  \nPresidente de la asamblea\n\n_______________________  \nSecretario\n\n"
        "> Borrador generado automáticamente a partir de una grabación. Debe ser revisado, corregido y aprobado por las personas designadas por la asamblea antes de ser considerado acta oficial."
    )
    return "\n".join(out)


class InProcessQueue:
    """Sustituto local de Azure Service Bus: cola en memoria con un worker asyncio."""

    def __init__(self) -> None:
        self._q: asyncio.Queue[str] | None = None
        self._task: asyncio.Task | None = None
        self._queued: set[str] = set()

    def start(self, service: RecordingService, pending: list[str]) -> None:
        self._q = asyncio.Queue()

        async def worker() -> None:
            assert self._q is not None
            while True:
                rid = await self._q.get()
                self._queued.discard(rid)
                try:
                    await service.process(rid)
                except Exception:  # noqa: BLE001
                    log.exception("worker")

        self._task = asyncio.create_task(worker())
        for rid in pending:
            self.enqueue(rid)

    def enqueue(self, rec_id: str) -> None:
        if self._q is None or rec_id in self._queued:
            return
        self._queued.add(rec_id)
        self._q.put_nowait(rec_id)

    def stop(self) -> None:
        if self._task:
            self._task.cancel()

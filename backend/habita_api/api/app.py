"""Adaptador de entrada HTTP (FastAPI)."""
from __future__ import annotations

import os
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import APIRouter, FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from habita_api.adapters.ai import build_ai_adapter
from habita_api.adapters.storage import FileAudioStore, SqliteRecordingRepository
from habita_api.application.services import Invalid, InProcessQueue, NotFound, RecordingService, transcript_lines
from habita_api.domain.models import Chunk, ChunkStatus, Marker, Recording, fmt_ts


class StartIn(BaseModel):
    meeting_id: int
    title: str
    community: str
    consent_by: str = Field(description="Persona que autoriza la grabación a nombre de la asamblea")
    context: dict = {}


class MarkerIn(BaseModel):
    t_ms: int = Field(ge=0)
    kind: str = Field(pattern="^(agenda|vote|note)$")
    label: str = Field(max_length=300)


class FinishIn(BaseModel):
    markers: list[MarkerIn] = []
    context: dict | None = None


class DraftIn(BaseModel):
    draft: str


def recording_json(rec: Recording, chunks: list[Chunk]) -> dict:
    done = sum(c.status == ChunkStatus.TRANSCRIBED for c in chunks)
    failed = sum(c.status == ChunkStatus.FAILED for c in chunks)
    return {
        "id": rec.id, "meeting_id": rec.meeting_id, "title": rec.title, "status": rec.status.value, "error": rec.error,
        "consent_by": rec.consent_by, "consent_at": rec.consent_at, "created_at": rec.created_at, "finished_at": rec.finished_at,
        "audio_deleted": rec.audio_deleted, "draft": rec.draft,
        "progress": {"chunks": len(chunks), "transcribed": done, "failed": failed},
        "transcript": [{"t_ms": t, "ts": fmt_ts(t), "text": x} for t, x in transcript_lines(chunks)],
        "markers": [{"t_ms": m.t_ms, "kind": m.kind, "label": m.label} for m in rec.markers],
    }


def create_app(service: RecordingService | None = None, queue: InProcessQueue | None = None, provider: str | None = None) -> FastAPI:
    data_dir = Path(os.environ.get("HABITA_DATA_DIR", Path(__file__).resolve().parents[2] / "data"))
    if service is None:
        ai = build_ai_adapter()
        queue = InProcessQueue()
        service = RecordingService(SqliteRecordingRepository(data_dir / "habita.db"), FileAudioStore(data_dir / "audio"), ai, ai, queue)
        provider = ai.name
    svc = service

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        if queue is not None:
            queue.start(svc, [r.id for r in svc.repo.list_pending()])
        yield
        if queue is not None:
            queue.stop()

    app = FastAPI(title="Habita API", lifespan=lifespan)
    api = APIRouter(prefix="/api")

    @api.get("/health")
    def health():
        return {"ok": True, "ai_provider": provider or svc.transcriber.name}

    @api.post("/recordings", status_code=201)
    def start(body: StartIn):
        rec = svc.start(body.meeting_id, body.title, body.community, body.consent_by, body.context)
        return recording_json(rec, [])

    @api.put("/recordings/{rec_id}/chunks/{seq}", status_code=204)
    async def upload(rec_id: str, seq: int, request: Request, start_ms: int = 0, duration_ms: int = 60000):
        data = await request.body()
        svc.upload_chunk(rec_id, seq, start_ms, duration_ms, request.headers.get("content-type", "audio/webm"), data)

    @api.post("/recordings/{rec_id}/finish")
    def finish(rec_id: str, body: FinishIn):
        rec = svc.finish(rec_id, [Marker(m.t_ms, m.kind, m.label) for m in body.markers], body.context)
        return recording_json(rec, svc.repo.chunks(rec_id))

    @api.get("/recordings/{rec_id}")
    def get(rec_id: str):
        return recording_json(*svc.view(rec_id))

    @api.post("/recordings/{rec_id}/regenerate")
    def regenerate(rec_id: str):
        rec = svc.regenerate(rec_id)
        return recording_json(rec, svc.repo.chunks(rec_id))

    @api.put("/recordings/{rec_id}/draft")
    def save_draft(rec_id: str, body: DraftIn):
        rec = svc.save_draft(rec_id, body.draft)
        return recording_json(rec, svc.repo.chunks(rec_id))

    @api.delete("/recordings/{rec_id}/audio")
    def delete_audio(rec_id: str):
        rec = svc.delete_audio(rec_id)
        return recording_json(rec, svc.repo.chunks(rec_id))

    @app.exception_handler(NotFound)
    async def _nf(_: Request, exc: NotFound):
        return JSONResponse({"detail": "Grabación no encontrada"}, status_code=404)

    @app.exception_handler(Invalid)
    async def _inv(_: Request, exc: Invalid):
        return JSONResponse({"detail": str(exc)}, status_code=422)

    app.include_router(api)
    return app


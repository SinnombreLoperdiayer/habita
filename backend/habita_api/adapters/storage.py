"""Adaptadores de persistencia. SQLite y disco local son sustitutos de Azure SQL y Blob Storage."""
from __future__ import annotations

import json
import shutil
import sqlite3
import threading
from dataclasses import asdict
from pathlib import Path

from habita_api.domain.models import Chunk, ChunkStatus, Marker, Recording, RecordingStatus

SCHEMA = """
CREATE TABLE IF NOT EXISTS recordings (
  id TEXT PRIMARY KEY, meeting_id INTEGER, title TEXT, community TEXT, consent_by TEXT, consent_at REAL,
  created_at REAL, status TEXT, finished_at REAL, markers TEXT, context TEXT, draft TEXT, error TEXT, audio_deleted INTEGER
);
CREATE TABLE IF NOT EXISTS chunks (
  recording_id TEXT, seq INTEGER, start_ms INTEGER, duration_ms INTEGER, mime TEXT, path TEXT,
  status TEXT, text TEXT, error TEXT, PRIMARY KEY (recording_id, seq)
);
"""


class SqliteRecordingRepository:
    def __init__(self, db_path: Path):
        db_path.parent.mkdir(parents=True, exist_ok=True)
        self._db = sqlite3.connect(db_path, check_same_thread=False)
        self._db.row_factory = sqlite3.Row
        self._lock = threading.Lock()
        with self._lock:
            self._db.executescript(SCHEMA)

    def add(self, rec: Recording) -> None:
        self.save(rec)

    def save(self, rec: Recording) -> None:
        with self._lock:
            self._db.execute(
                "INSERT OR REPLACE INTO recordings VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                (rec.id, rec.meeting_id, rec.title, rec.community, rec.consent_by, rec.consent_at, rec.created_at, rec.status.value,
                 rec.finished_at, json.dumps([asdict(m) for m in rec.markers]), json.dumps(rec.context), rec.draft, rec.error, int(rec.audio_deleted)),
            )
            self._db.commit()

    def get(self, rec_id: str) -> Recording | None:
        with self._lock:
            row = self._db.execute("SELECT * FROM recordings WHERE id=?", (rec_id,)).fetchone()
        return self._rec(row) if row else None

    def list_pending(self) -> list[Recording]:
        with self._lock:
            rows = self._db.execute("SELECT * FROM recordings WHERE status IN ('queued','transcribing','drafting')").fetchall()
        return [self._rec(r) for r in rows]

    def upsert_chunk(self, c: Chunk) -> None:
        with self._lock:
            self._db.execute(
                "INSERT OR REPLACE INTO chunks VALUES (?,?,?,?,?,?,?,?,?)",
                (c.recording_id, c.seq, c.start_ms, c.duration_ms, c.mime, c.path, c.status.value, c.text, c.error),
            )
            self._db.commit()

    def chunks(self, rec_id: str) -> list[Chunk]:
        with self._lock:
            rows = self._db.execute("SELECT * FROM chunks WHERE recording_id=? ORDER BY seq", (rec_id,)).fetchall()
        return [Chunk(r["recording_id"], r["seq"], r["start_ms"], r["duration_ms"], r["mime"], r["path"], ChunkStatus(r["status"]), r["text"], r["error"]) for r in rows]

    @staticmethod
    def _rec(r: sqlite3.Row) -> Recording:
        return Recording(
            id=r["id"], meeting_id=r["meeting_id"], title=r["title"], community=r["community"], consent_by=r["consent_by"],
            consent_at=r["consent_at"], created_at=r["created_at"], status=RecordingStatus(r["status"]), finished_at=r["finished_at"],
            markers=[Marker(**m) for m in json.loads(r["markers"] or "[]")], context=json.loads(r["context"] or "{}"),
            draft=r["draft"] or "", error=r["error"] or "", audio_deleted=bool(r["audio_deleted"]),
        )


class FileAudioStore:
    def __init__(self, root: Path):
        self.root = root
        root.mkdir(parents=True, exist_ok=True)

    def save(self, rec_id: str, seq: int, data: bytes, ext: str) -> str:
        d = self.root / rec_id
        d.mkdir(parents=True, exist_ok=True)
        p = d / f"{seq:05d}.{ext}"
        p.write_bytes(data)
        return str(p)

    def read(self, path: str) -> bytes:
        return Path(path).read_bytes()

    def delete_recording(self, rec_id: str) -> None:
        shutil.rmtree(self.root / rec_id, ignore_errors=True)

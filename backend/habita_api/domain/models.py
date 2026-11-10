"""Dominio puro: sin dependencias de frameworks ni de infraestructura."""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum


class RecordingStatus(StrEnum):
    RECORDING = "recording"
    QUEUED = "queued"
    TRANSCRIBING = "transcribing"
    DRAFTING = "drafting"
    READY = "ready"
    FAILED = "failed"


class ChunkStatus(StrEnum):
    UPLOADED = "uploaded"
    TRANSCRIBED = "transcribed"
    FAILED = "failed"


@dataclass
class Marker:
    t_ms: int
    kind: str  # "agenda" | "vote" | "note"
    label: str


@dataclass
class Chunk:
    recording_id: str
    seq: int
    start_ms: int
    duration_ms: int
    mime: str
    path: str
    status: ChunkStatus = ChunkStatus.UPLOADED
    text: str = ""
    error: str = ""

    @property
    def mid_ms(self) -> int:
        return self.start_ms + self.duration_ms // 2


@dataclass
class Recording:
    id: str
    meeting_id: int
    title: str
    community: str
    consent_by: str
    consent_at: float
    created_at: float
    status: RecordingStatus = RecordingStatus.RECORDING
    finished_at: float | None = None
    markers: list[Marker] = field(default_factory=list)
    context: dict = field(default_factory=dict)
    draft: str = ""
    error: str = ""
    audio_deleted: bool = False


def fmt_ts(ms: int) -> str:
    s = max(0, ms) // 1000
    return f"{s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}"

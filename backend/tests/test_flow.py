import time

import pytest
from fastapi.testclient import TestClient

from habita_api.adapters.storage import FileAudioStore, SqliteRecordingRepository
from habita_api.api.app import create_app
from habita_api.application.services import InProcessQueue, RecordingService


class FakeAI:
    name = "fake"

    def __init__(self, fail_seq: set[str] | None = None):
        self.calls: list[str] = []
        self.fail = fail_seq or set()

    async def transcribe(self, audio, filename, mime, prompt):
        if any(f in filename for f in self.fail):
            raise RuntimeError("boom")
        return f"texto de {filename}"

    async def generate(self, system, user):
        self.calls.append(user)
        return "Se trató el punto. [00:00:05]"


@pytest.fixture
def make(tmp_path):
    def _make(ai):
        q = InProcessQueue()
        svc = RecordingService(SqliteRecordingRepository(tmp_path / "t.db"), FileAudioStore(tmp_path / "a"), ai, ai, q)
        return TestClient(create_app(svc, q, "fake")), ai

    return _make


def wait_ready(c, rid, want=("ready", "failed")):
    for _ in range(80):
        j = c.get(f"/api/recordings/{rid}").json()
        if j["status"] in want:
            return j
        time.sleep(0.1)
    raise AssertionError(j)


def start(c):
    r = c.post("/api/recordings", json={"meeting_id": 1, "title": "Asamblea", "community": "Los Olivos", "consent_by": "Presidente",
                                         "context": {"date": "2026-11-09", "attendance": {"present": 10, "total": 20, "percent": 50},
                                                     "polls": [{"title": "Presupuesto", "yes": 8, "no": 1, "abstain": 1}]}})
    assert r.status_code == 201
    return r.json()["id"]


def test_consent_required(make):
    with make(FakeAI())[0] as c:
        r = c.post("/api/recordings", json={"meeting_id": 1, "title": "x", "community": "y", "consent_by": " "})
        assert r.status_code == 422


def test_full_flow_sections_by_marker(make):
    with make(FakeAI())[0] as c:
        rid = start(c)
        for seq in range(3):
            r = c.put(f"/api/recordings/{rid}/chunks/{seq}?start_ms={seq * 60000}&duration_ms=60000", content=b"abc", headers={"content-type": "audio/webm;codecs=opus"})
            assert r.status_code == 204
        c.post(f"/api/recordings/{rid}/finish", json={"markers": [
            {"t_ms": 0, "kind": "agenda", "label": "Quórum"},
            {"t_ms": 70000, "kind": "agenda", "label": "Presupuesto"},
            {"t_ms": 100000, "kind": "vote", "label": "Votación abierta"},
        ]})
        j = wait_ready(c, rid)
        assert j["status"] == "ready", j
        assert j["progress"] == {"chunks": 3, "transcribed": 3, "failed": 0}
        assert "### 1. Quórum" in j["draft"] and "### 2. Presupuesto" in j["draft"]
        assert "| Presupuesto | 8 | 1 | 1 |" in j["draft"]
        assert "[EVENTO · votación]" not in j["draft"]  # el evento va al modelo, no al borrador


def test_marker_routing(make):
    client, ai = make(FakeAI())
    with client as c:
        rid = start(c)
        for seq in range(3):
            c.put(f"/api/recordings/{rid}/chunks/{seq}?start_ms={seq * 60000}&duration_ms=60000", content=b"abc", headers={"content-type": "audio/webm"})
        c.post(f"/api/recordings/{rid}/finish", json={"markers": [{"t_ms": 0, "kind": "agenda", "label": "A"}, {"t_ms": 90000, "kind": "agenda", "label": "B"}]})
        wait_ready(c, rid)
    a = next(u for u in ai.calls if "Punto del orden del día: A" in u)
    b = next(u for u in ai.calls if "Punto del orden del día: B" in u)
    assert "-0.webm" in a and "-1.webm" not in a  # punto medio del fragmento 0 = 30 s
    assert "-1.webm" in b and "-2.webm" in b  # punto medio del fragmento 1 = 90 s, ya pertenece a B


def test_partial_failure_still_drafts(make):
    with make(FakeAI(fail_seq={"-1."}))[0] as c:
        rid = start(c)
        for seq in range(2):
            c.put(f"/api/recordings/{rid}/chunks/{seq}?start_ms={seq * 60000}", content=b"abc", headers={"content-type": "audio/webm"})
        c.post(f"/api/recordings/{rid}/finish", json={"markers": []})
        j = wait_ready(c, rid)
        assert j["status"] == "ready" and j["progress"]["failed"] == 1


def test_all_fail_marks_failed(make):
    with make(FakeAI(fail_seq={"-"}))[0] as c:
        rid = start(c)
        c.put(f"/api/recordings/{rid}/chunks/0", content=b"abc", headers={"content-type": "audio/webm"})
        c.post(f"/api/recordings/{rid}/finish", json={"markers": []})
        j = wait_ready(c, rid)
        assert j["status"] == "failed" and "boom" in j["error"]


def test_delete_audio_and_edit_draft(make):
    with make(FakeAI())[0] as c:
        rid = start(c)
        c.put(f"/api/recordings/{rid}/chunks/0", content=b"abc", headers={"content-type": "audio/webm"})
        c.post(f"/api/recordings/{rid}/finish", json={"markers": []})
        wait_ready(c, rid)
        assert c.put(f"/api/recordings/{rid}/draft", json={"draft": "editado"}).json()["draft"] == "editado"
        assert c.delete(f"/api/recordings/{rid}/audio").json()["audio_deleted"] is True
        assert c.put(f"/api/recordings/{rid}/chunks/1", content=b"x", headers={"content-type": "audio/webm"}).status_code == 422
        assert c.get("/api/recordings/nope").status_code == 404

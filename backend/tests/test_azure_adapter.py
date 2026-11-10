"""Valida el adaptador de Azure OpenAI contra un servidor Azure falso (rutas, versión de API, deployments)."""
import asyncio
import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest
from openai import AsyncAzureOpenAI

from habita_api.adapters.ai import AzureOpenAIAdapter, build_ai_adapter

seen: list[dict] = []


class Fake(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("content-length", 0)))
        seen.append({"path": self.path, "key": self.headers.get("api-key"), "ctype": self.headers.get("content-type", ""), "body": body})
        if "/audio/transcriptions" in self.path:
            out = {"text": "texto transcrito"}
        else:
            out = {"id": "x", "object": "chat.completion", "created": 0, "model": "m",
                   "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant", "content": "borrador"}}]}
        data = json.dumps(out).encode()
        self.send_response(200)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


@pytest.fixture
def server():
    srv = HTTPServer(("127.0.0.1", 0), Fake)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    seen.clear()
    yield f"http://127.0.0.1:{srv.server_port}"
    srv.shutdown()


def test_azure_adapter_uses_deployment_paths(server):
    client = AsyncAzureOpenAI(azure_endpoint=server, api_key="k", api_version="2025-04-01-preview")
    ad = AzureOpenAIAdapter(client, "mi-transcribe", "mi-chat")

    async def run():
        return await ad.transcribe(b"RIFFxxxx", "a-0.webm", "audio/webm", "contexto"), await ad.generate("sys", "user")

    text, draft = asyncio.run(run())
    assert (text, draft) == ("texto transcrito", "borrador")
    t, c = seen
    assert t["path"].startswith("/openai/deployments/mi-transcribe/audio/transcriptions") and "api-version=2025-04-01-preview" in t["path"]
    assert c["path"].startswith("/openai/deployments/mi-chat/chat/completions")
    assert t["key"] == c["key"] == "k"
    assert t["ctype"].startswith("multipart/form-data") and b'name="language"' in t["body"] and b"es" in t["body"]


def test_env_selects_azure(monkeypatch):
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://x.openai.azure.com")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "k")
    monkeypatch.setenv("AZURE_OPENAI_TRANSCRIBE_DEPLOYMENT", "t1")
    monkeypatch.setenv("AZURE_OPENAI_CHAT_DEPLOYMENT", "c1")
    ad = build_ai_adapter()
    assert ad.name == "azure-openai" and ad.transcribe_deployment == "t1" and ad.chat_deployment == "c1"

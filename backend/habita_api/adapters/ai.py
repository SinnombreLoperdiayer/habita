"""Adaptadores de IA. Azure OpenAI es el proveedor objetivo; sin él se usa un modo demostración."""
from __future__ import annotations

import os
from dataclasses import dataclass

from openai import AsyncAzureOpenAI, AsyncOpenAI


@dataclass
class AzureOpenAIAdapter:
    """Azure OpenAI: `model` es el NOMBRE DEL DEPLOYMENT creado en Azure AI Foundry."""

    client: AsyncAzureOpenAI
    transcribe_deployment: str
    chat_deployment: str
    name: str = "azure-openai"

    async def transcribe(self, audio: bytes, filename: str, mime: str, prompt: str) -> str:
        res = await self.client.audio.transcriptions.create(
            model=self.transcribe_deployment, file=(filename, audio, mime), language="es", prompt=prompt, response_format="json"
        )
        return res.text

    async def generate(self, system: str, user: str) -> str:
        res = await self.client.chat.completions.create(
            model=self.chat_deployment, messages=[{"role": "system", "content": system}, {"role": "user", "content": user}]
        )
        return res.choices[0].message.content or ""


DEMO_PHRASES = [
    "Buenas noches a todos, siendo las seis de la tarde damos inicio a la asamblea general ordinaria. Hay quórum para deliberar y decidir.",
    "Propongo como presidente de la asamblea al señor del apartamento A ciento dos y como secretaria a la señora del apartamento B trescientos cuatro.",
    "El administrador presenta el informe de gestión: se ejecutó el noventa por ciento del presupuesto y se renovó el sistema de cámaras de seguridad.",
    "Se somete a consideración el presupuesto del año siguiente, con un incremento del seis por ciento en las cuotas de administración.",
    "Un copropietario solicita que el parque infantil se incluya en la renovación de zonas comunes. El consejo toma nota de la proposición.",
]


@dataclass
class DemoAdapter:
    """Sin Azure configurado: el texto lo redacta el proxy de OpenAI de Workshop, pero la
    transcripción es SIMULADA (el proxy no ofrece voz a texto compatible). Solo para demostración."""

    client: AsyncOpenAI
    chat_model: str = "gpt-5.4-mini"
    name: str = "demo"

    async def transcribe(self, audio: bytes, filename: str, mime: str, prompt: str) -> str:
        try:
            n = int(filename.rsplit("-", 1)[-1].split(".")[0])
        except ValueError:
            n = 0
        return f"[SIMULADO] {DEMO_PHRASES[n % len(DEMO_PHRASES)]}"

    async def generate(self, system: str, user: str) -> str:
        res = await self.client.responses.create(
            model=self.chat_model, input=[{"role": "system", "content": system}, {"role": "user", "content": user}]
        )
        return res.output_text


class UnconfiguredAdapter:
    name = "none"

    async def transcribe(self, audio: bytes, filename: str, mime: str, prompt: str) -> str:
        raise RuntimeError("No hay proveedor de IA configurado (define AZURE_OPENAI_*).")

    async def generate(self, system: str, user: str) -> str:
        raise RuntimeError("No hay proveedor de IA configurado (define AZURE_OPENAI_*).")


def build_ai_adapter() -> AzureOpenAIAdapter | DemoAdapter | UnconfiguredAdapter:
    endpoint, key = os.environ.get("AZURE_OPENAI_ENDPOINT"), os.environ.get("AZURE_OPENAI_API_KEY")
    if endpoint and key:
        client = AsyncAzureOpenAI(
            azure_endpoint=endpoint, api_key=key, api_version=os.environ.get("AZURE_OPENAI_API_VERSION", "2025-04-01-preview"), timeout=120
        )
        return AzureOpenAIAdapter(
            client,
            os.environ.get("AZURE_OPENAI_TRANSCRIBE_DEPLOYMENT", "gpt-4o-transcribe"),
            os.environ.get("AZURE_OPENAI_CHAT_DEPLOYMENT", "gpt-4o"),
        )
    wk, base = os.environ.get("OPENAI_WORKSHOP_API_KEY"), os.environ.get("OPENAI_WORKSHOP_BASE_URL")
    if wk and base:
        return DemoAdapter(AsyncOpenAI(api_key=wk, base_url=base, timeout=120))
    return UnconfiguredAdapter()


"""Text generation providers. The model only returns text; it has no tools and cannot act on data."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Protocol

import httpx

from .config import TOKEN_PARAMETERS, Settings
from .embeddings import ProviderError


@dataclass
class Completion:
    text: str
    prompt_tokens: int
    completion_tokens: int
    model: str


@dataclass(frozen=True)
class GenerationOptions:
    """Per-request generation values (from the API's active RAG configuration).

    model: provider model id; temperature: None means the field is omitted from the provider call (models without
    temperature support); token_parameter: "max_tokens" (classic chat models) or "max_completion_tokens" (reasoning-style models).
    """

    model: str
    temperature: Optional[float]
    token_parameter: str = "max_tokens"  # noqa: S105 - an API field name, not a secret


class ChatModel(Protocol):
    model: str

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None) -> Completion: ...


def estimate_tokens(text: str) -> int:
    # Conservative for Vietnamese (more tokens per character than English).
    return max(1, len(text) // 3)


class OpenAICompatibleChatModel:
    def __init__(self, settings: Settings) -> None:
        self.model = settings.llm_model
        self._temperature = settings.llm_temperature
        self._client = httpx.Client(
            base_url=(settings.llm_base_url or "").rstrip("/") + "/",
            headers={"Authorization": f"Bearer {settings.llm_api_key}"},
            timeout=settings.provider_timeout_seconds,
        )

    def payload(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None) -> Dict[str, Any]:
        """Request body: the token limit goes under the model's token parameter; temperature is left out when None."""
        if options is None:
            options = GenerationOptions(self.model, self._temperature, "max_tokens")
        if options.token_parameter not in TOKEN_PARAMETERS:
            raise ValueError("unsupported token parameter")
        body: Dict[str, Any] = {"model": options.model, "messages": messages, options.token_parameter: max_tokens}
        if options.temperature is not None:
            body["temperature"] = options.temperature
        return body

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None) -> Completion:
        body = self.payload(messages, max_tokens, options)
        try:
            response = self._client.post("chat/completions", json=body)
        except httpx.HTTPError as exc:
            raise ProviderError(f"LLM provider unreachable ({type(exc).__name__})") from exc
        if response.status_code >= 400:
            raise ProviderError(f"LLM provider returned HTTP {response.status_code}")
        reply = response.json()
        choices = reply.get("choices") or []
        text = ((choices[0].get("message") or {}).get("content") or "") if choices else ""
        usage = reply.get("usage") or {}
        return Completion(
            text=text,
            prompt_tokens=int(usage.get("prompt_tokens") or sum(estimate_tokens(m["content"]) for m in messages)),
            completion_tokens=int(usage.get("completion_tokens") or estimate_tokens(text)),
            model=str(reply.get("model") or body["model"]),
        )


_SOURCE = re.compile(r'<source id="(\d+)"[^>]*>\n?(.*?)\n?</source>', re.DOTALL)


class ExtractiveChatModel:
    """Development/test model: quotes the best source sentence with its citation. Never calls a network."""

    def __init__(self, model: str = "extractive-dev") -> None:
        self.model = model

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None) -> Completion:
        # Offline stand-in: generation options do not change its output (it never calls a provider).
        prompt = messages[-1]["content"]
        sources = _SOURCE.findall(prompt)
        if not sources:
            text = "INSUFFICIENT_CONTEXT"
        else:
            number, content = sources[0]
            sentence = re.split(r"(?<=[.!?…])\s+", content.strip())[0][:600]
            text = f"{sentence} [{number}]"
        return Completion(text, sum(estimate_tokens(m["content"]) for m in messages), estimate_tokens(text), self.model)


def create_chat_model(settings: Settings) -> ChatModel:
    if settings.llm_provider == "openai":
        return OpenAICompatibleChatModel(settings)
    return ExtractiveChatModel(settings.llm_model)

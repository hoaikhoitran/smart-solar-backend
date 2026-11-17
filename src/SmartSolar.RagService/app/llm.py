"""Text generation providers. The model only returns text; it has no tools and cannot act on data."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Protocol

import httpx

from .config import REASONING_EFFORTS, TOKEN_PARAMETERS, Settings
from .embeddings import ProviderError, ProviderTimeout, ollama_model_available, ollama_options, status_error, transport_error


def _timeout(seconds: Optional[float]) -> Dict[str, Any]:
    """Per-call timeout: the caller's remaining deadline when it is shorter than the client's default (provider timeout).

    Closing the connection when the API has given up makes Ollama stop the generation instead of finishing an answer nobody waits for.
    """
    return {} if seconds is None else {"timeout": max(1.0, float(seconds))}


@dataclass
class Completion:
    text: str
    prompt_tokens: int
    completion_tokens: int
    model: str
    # Provider-reported breakdown in milliseconds (Ollama: loadMs, promptEvalMs, evalMs, totalMs); None when the provider gives none.
    timings: Optional[Dict[str, int]] = None
    # Of prompt_tokens, how many the provider served from its prompt cache (billed at the cached rate); of completion_tokens, how
    # many were reasoning tokens (billed as output). usage_reported is False when the provider gave no usage (counts are estimates).
    cached_prompt_tokens: int = 0
    reasoning_tokens: int = 0
    usage_reported: bool = True
    # "openai" is billed; "ollama" and "extractive" are not.
    provider: str = "extractive"


@dataclass(frozen=True)
class GenerationOptions:
    """Per-request generation values (from the API's active RAG configuration).

    model: provider model id; temperature: None means the field is omitted from the provider call (models without
    temperature support); token_parameter: "max_tokens" (classic chat models) or "max_completion_tokens" (reasoning-style models);
    reasoning_effort: Chat Completions ``reasoning_effort`` (None = not sent). With an effort other than "none" the temperature is
    never sent (OpenAI GPT-6 models reject temperature/top_p unless reasoning is off).
    """

    model: str
    temperature: Optional[float]
    token_parameter: str = "max_tokens"  # noqa: S105 - an API field name, not a secret
    reasoning_effort: Optional[str] = None


class ChatModel(Protocol):
    model: str

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None,
                 timeout_seconds: Optional[float] = None) -> Completion: ...


def estimate_tokens(text: str) -> int:
    # Conservative for Vietnamese (more tokens per character than English).
    return max(1, len(text) // 3)


class OpenAICompatibleChatModel:
    """OpenAI Chat Completions. One call per question: never retried with another model, never streamed, no tools."""

    provider = "openai"

    def __init__(self, settings: Settings, transport: Optional[httpx.BaseTransport] = None) -> None:
        self.model = settings.llm_model
        self._temperature = settings.llm_temperature
        self._client = httpx.Client(
            base_url=(settings.llm_base_url or "").rstrip("/") + "/",
            headers={"Authorization": f"Bearer {settings.llm_api_key}"},
            timeout=settings.provider_timeout_seconds,
            transport=transport,
        )

    def payload(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None) -> Dict[str, Any]:
        """Request body: the token limit goes under the model's token parameter; temperature is left out when None."""
        if options is None:
            options = GenerationOptions(self.model, self._temperature, "max_tokens")
        if options.token_parameter not in TOKEN_PARAMETERS:
            raise ValueError("unsupported token parameter")
        if options.reasoning_effort is not None and options.reasoning_effort not in REASONING_EFFORTS:
            raise ValueError("unsupported reasoning effort")
        body: Dict[str, Any] = {"model": options.model, "messages": messages, options.token_parameter: max_tokens}
        if options.reasoning_effort is not None:
            body["reasoning_effort"] = options.reasoning_effort
        reasoning_on = options.reasoning_effort not in (None, "none")
        if options.temperature is not None and not reasoning_on:
            body["temperature"] = options.temperature
        return body

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None,
                 timeout_seconds: Optional[float] = None) -> Completion:
        body = self.payload(messages, max_tokens, options)
        try:
            response = self._client.post("chat/completions", json=body, **_timeout(timeout_seconds))
        except httpx.HTTPError as exc:
            raise transport_error(exc, "LLM provider") from exc
        if response.status_code >= 400:
            raise status_error(response.status_code, "LLM provider")
        try:
            reply = response.json()
        except ValueError as exc:
            error = ProviderError("LLM provider returned a non-JSON body")
            error.billing = "uncertain"
            raise error from exc
        choices = reply.get("choices") or []
        text = ((choices[0].get("message") or {}).get("content") or "") if choices else ""
        usage = reply.get("usage") if isinstance(reply.get("usage"), dict) else {}
        reported = _count(usage.get("prompt_tokens")) is not None and _count(usage.get("completion_tokens")) is not None
        prompt_details = usage.get("prompt_tokens_details") if isinstance(usage.get("prompt_tokens_details"), dict) else {}
        completion_details = usage.get("completion_tokens_details") if isinstance(usage.get("completion_tokens_details"), dict) else {}
        return Completion(
            text=text,
            # Without reported usage the estimate is conservative: the full output budget is assumed generated.
            prompt_tokens=_count(usage.get("prompt_tokens")) or sum(estimate_tokens(m["content"]) for m in messages),
            completion_tokens=_count(usage.get("completion_tokens")) if reported else max(max_tokens, estimate_tokens(text)),
            model=str(reply.get("model") or body["model"]),
            cached_prompt_tokens=_count(prompt_details.get("cached_tokens")) or 0,
            reasoning_tokens=_count(completion_details.get("reasoning_tokens")) or 0,
            usage_reported=reported,
            provider=self.provider,
        )


def _count(value: Any) -> Optional[int]:
    return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else None


_THINKING = re.compile(r"<think>.*?</think>", re.DOTALL)


class OllamaChatModel:
    """Ollama's native /api/chat (local inference, no API key).

    The native API, not the OpenAI-compatible one: only it can switch off Qwen3 "thinking" (``think``) and set the context
    window (``options.num_ctx``; Ollama otherwise truncates the prompt silently). The token limit becomes ``num_predict``,
    whatever token parameter the request names (an OpenAI concept). A thinking block is never returned as the answer.
    """

    provider = "ollama"

    def __init__(self, settings: Settings, transport: Optional[httpx.BaseTransport] = None) -> None:
        self.model = settings.llm_model
        self._temperature = settings.llm_temperature
        self._context_tokens = settings.llm_context_tokens
        self._keep_alive = settings.ollama_keep_alive
        self._options = ollama_options(settings)
        self._client = httpx.Client(
            base_url=(settings.llm_base_url or "").rstrip("/") + "/",
            timeout=settings.provider_timeout_seconds,
            transport=transport,
        )

    def available(self) -> bool:
        return ollama_model_available(self._client, self.model)

    def payload(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None) -> Dict[str, Any]:
        if options is None:
            options = GenerationOptions(self.model, self._temperature, "max_tokens")
        settings: Dict[str, Any] = {"num_predict": max_tokens, "num_ctx": self._context_tokens, **self._options}
        if options.temperature is not None:
            settings["temperature"] = options.temperature
        return {"model": options.model, "messages": messages, "stream": False, "think": False, "keep_alive": self._keep_alive, "options": settings}

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None,
                 timeout_seconds: Optional[float] = None) -> Completion:
        body = self.payload(messages, max_tokens, options)
        try:
            response = self._client.post("api/chat", json=body, **_timeout(timeout_seconds))
        except httpx.TimeoutException as exc:
            raise ProviderTimeout("LLM provider timed out") from exc
        except httpx.HTTPError as exc:
            raise ProviderError(f"LLM provider unreachable ({type(exc).__name__})") from exc
        if response.status_code >= 400:
            # 404 also means "model not pulled"; the body is not echoed.
            raise ProviderError(f"LLM provider returned HTTP {response.status_code}")
        try:
            reply = response.json()
        except ValueError as exc:
            raise ProviderError("LLM provider returned a non-JSON body") from exc
        message = reply.get("message") if isinstance(reply, dict) else None
        content = message.get("content") if isinstance(message, dict) else None
        if not isinstance(content, str):
            raise ProviderError("LLM provider returned no message content")
        text = _THINKING.sub("", content).strip()
        return Completion(
            text=text,
            prompt_tokens=int(reply.get("prompt_eval_count") or sum(estimate_tokens(m["content"]) for m in messages)),
            completion_tokens=int(reply.get("eval_count") or estimate_tokens(text)),
            model=str(reply.get("model") or body["model"]),
            timings=ollama_timings(reply),
            provider=self.provider,
        )


_DURATIONS = (("loadMs", "load_duration"), ("promptEvalMs", "prompt_eval_duration"), ("evalMs", "eval_duration"), ("totalMs", "total_duration"))


def ollama_timings(reply: Dict[str, Any]) -> Optional[Dict[str, int]]:
    """Ollama's nanosecond durations as milliseconds. On a CPU-only server prompt evaluation (the retrieved context) usually costs
    more than generation; this breakdown is what tells the two apart in the service log."""
    timings = {key: int(reply[field]) // 1_000_000 for key, field in _DURATIONS if isinstance(reply.get(field), int) and not isinstance(reply.get(field), bool)}
    return timings or None


_SOURCE = re.compile(r'<source id="(\d+)"[^>]*>\n?(.*?)\n?</source>', re.DOTALL)


class ExtractiveChatModel:
    """Development/test model: quotes the best source sentence with its citation. Never calls a network."""

    def __init__(self, model: str = "extractive-dev") -> None:
        self.model = model

    def complete(self, messages: List[Dict[str, str]], max_tokens: int, options: Optional[GenerationOptions] = None,
                 timeout_seconds: Optional[float] = None) -> Completion:
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
    if settings.llm_provider == "ollama":
        return OllamaChatModel(settings)
    return ExtractiveChatModel(settings.llm_model)

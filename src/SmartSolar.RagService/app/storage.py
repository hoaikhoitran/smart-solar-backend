"""Read-only access to the uploaded source object. The API already verified ownership, type and checksum;
this service re-checks the size and SHA-256 of what it actually downloads."""

from __future__ import annotations

import os
from typing import Optional, Protocol

from .config import Settings


class StorageError(Exception):
    """Transient or configuration problem reaching storage (the job is retried)."""


class ObjectTooLarge(Exception):
    pass


class ObjectStorage(Protocol):
    def read(self, bucket: str, key: str, version_id: Optional[str], max_bytes: int) -> bytes: ...


def _safe_key(key: str) -> None:
    if not key or key.startswith("/") or ".." in key.split("/") or "\\" in key or len(key) > 1024:
        raise StorageError("invalid object key")


class S3Storage:
    def __init__(self, settings: Settings) -> None:
        import boto3
        from botocore.config import Config

        self._allowed = set(settings.s3_allowed_buckets)
        self._client = boto3.client(
            "s3",
            region_name=settings.s3_region,
            endpoint_url=settings.s3_endpoint_url,
            config=Config(connect_timeout=10, read_timeout=60, retries={"max_attempts": 3}),
        )

    def read(self, bucket: str, key: str, version_id: Optional[str], max_bytes: int) -> bytes:
        if bucket not in self._allowed:
            raise StorageError("bucket not allowed")
        _safe_key(key)
        from botocore.exceptions import BotoCoreError, ClientError

        try:
            args = {"Bucket": bucket, "Key": key}
            if version_id:
                args["VersionId"] = version_id
            response = self._client.get_object(**args)
            if int(response.get("ContentLength") or 0) > max_bytes:
                raise ObjectTooLarge()
            data = response["Body"].read(max_bytes + 1)
        except (BotoCoreError, ClientError) as exc:
            raise StorageError(type(exc).__name__) from exc
        if len(data) > max_bytes:
            raise ObjectTooLarge()
        return data


class LocalStorage:
    """Development/test only: objects at {root}/{bucket}/{key}."""

    def __init__(self, root: str) -> None:
        self._root = os.path.realpath(root)

    def read(self, bucket: str, key: str, version_id: Optional[str], max_bytes: int) -> bytes:
        _safe_key(key)
        if not bucket or "/" in bucket or bucket.startswith("."):
            raise StorageError("invalid bucket")
        path = os.path.realpath(os.path.join(self._root, bucket, key))
        if not path.startswith(self._root + os.sep):
            raise StorageError("invalid object path")
        if not os.path.isfile(path):
            raise StorageError("object not found")
        if os.path.getsize(path) > max_bytes:
            raise ObjectTooLarge()
        with open(path, "rb") as handle:
            return handle.read(max_bytes + 1)


def create_storage(settings: Settings) -> ObjectStorage:
    return S3Storage(settings) if settings.storage == "s3" else LocalStorage(settings.local_storage_dir)

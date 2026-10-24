"""Mapping engine output onto the wire contract's exact shape.

The merged adapter parses responses strictly: exactly ``width``, ``height``,
``regions`` (each region exactly ``label``/``x0``/``y0``/``x1``/``y1``), and
optional ``engine``/``model``/``version``; coordinates in [0, 1] with
``x0 <= x1`` and ``y0 <= y1``; labels matching ``[a-z0-9_-]{1,32}``; at most
4096 regions and a body under 1 MiB. Unknown properties are rejected
outright, so the response is built here, key by key, and nowhere else.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from .engine import Detection

MAX_REGIONS = 4096
MAX_LABEL_LENGTH = 32
MAX_ENGINE_INFO_LENGTH = 128
MAX_RESPONSE_BYTES = 1024 * 1024

FALLBACK_LABEL = "other"
_LABEL_PATTERN = re.compile(r"^[a-z0-9_-]{1,32}$")


class OutputContractError(RuntimeError):
    """Raised when engine output cannot be represented on the wire honestly."""


@dataclass
class RegionOut:
    label: str
    x0: float
    y0: float
    x1: float
    y1: float


@dataclass
class ResponseOut:
    width: int
    height: int
    regions: list[RegionOut] = field(default_factory=list)
    engine: str | None = None
    model: str | None = None
    version: str | None = None

    def to_json_dict(self) -> dict:
        """The exact property set the adapter accepts, and nothing else."""
        body: dict = {
            "width": self.width,
            "height": self.height,
            "regions": [
                {
                    "label": region.label,
                    "x0": region.x0,
                    "y0": region.y0,
                    "x1": region.x1,
                    "y1": region.y1,
                }
                for region in self.regions
            ],
        }
        # The three informational strings are optional on the wire; they are
        # emitted only when present, never as null.
        for name in ("engine", "model", "version"):
            value = getattr(self, name)
            if value is not None:
                body[name] = value
        return body


def map_label(raw: object) -> str:
    """Map an engine category label onto the generic ``[a-z0-9_-]`` set.

    Known MinerU categories already are short generic lowercase identifiers
    and are kept as they are, peripheral ones (headers, footers, page numbers,
    ...) included — peripheral content is labelled, never discarded. Anything
    the charset or length rules would reject maps to the stable generic label
    ``other`` rather than being dropped or truncated into something that lies.
    """
    if not isinstance(raw, str):
        return FALLBACK_LABEL
    candidate = raw.strip().lower().replace(" ", "_")
    if candidate == "unknown":
        return FALLBACK_LABEL
    if _LABEL_PATTERN.match(candidate):
        return candidate
    return FALLBACK_LABEL


def normalize_box(x0: float, y0: float, x1: float, y1: float) -> tuple[float, float, float, float]:
    """Clamp an engine box into [0, 1] with x0 <= x1 and y0 <= y1."""
    values = (x0, y0, x1, y1)
    if not all(isinstance(v, (int, float)) for v in values):
        raise OutputContractError("a region coordinate is not a number.")
    if not all(v == v and abs(v) != float("inf") for v in values):  # NaN/inf guard
        raise OutputContractError("a region coordinate is not finite.")
    left, right = sorted((min(max(float(x0), 0.0), 1.0), min(max(float(x1), 0.0), 1.0)))
    top, bottom = sorted((min(max(float(y0), 0.0), 1.0), min(max(float(y1), 0.0), 1.0)))
    return left, top, right, bottom


def build_response(
    detection: Detection,
    engine_name: str,
) -> ResponseOut:
    """Turn one engine detection into the wire response, or fail honestly.

    Raises :class:`OutputContractError` when the engine output cannot be
    represented within the merged contract (more than 4096 regions, or an
    informational string that would not survive the adapter's bound).
    """
    if detection.width <= 0 or detection.height <= 0:
        raise OutputContractError("the engine reported non-positive image dimensions.")
    if len(detection.regions) > MAX_REGIONS:
        raise OutputContractError(
            f"the engine returned {len(detection.regions)} regions; the wire "
            f"contract allows at most {MAX_REGIONS}.")

    regions: list[RegionOut] = []
    for label, x0, y0, x1, y1 in detection.regions:
        left, top, right, bottom = normalize_box(x0, y0, x1, y1)
        regions.append(RegionOut(map_label(label), left, top, right, bottom))

    return ResponseOut(
        width=detection.width,
        height=detection.height,
        regions=regions,
        engine=_bounded_info(engine_name, "engine"),
        model=_bounded_info(detection.model, "model"),
        version=_bounded_info(detection.version, "version"),
    )


def _bounded_info(value: str | None, field_name: str) -> str | None:
    if value is None:
        return None
    cleaned = "".join(c for c in value if ord(c) >= 32).strip()
    if not cleaned:
        return None
    if len(cleaned) > MAX_ENGINE_INFO_LENGTH:
        raise OutputContractError(
            f"the {field_name} string exceeds the {MAX_ENGINE_INFO_LENGTH}-character wire bound.")
    return cleaned

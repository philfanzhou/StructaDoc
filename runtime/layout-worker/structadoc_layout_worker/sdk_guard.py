"""The full-parsing guard installed on every SDK client this worker creates.

The worker exists to run the SDK's layout-only path and nothing else
(ADR-0013, point 2). The guard replaces every content-recognition and
full-parsing entry point on the client instance with a callable that raises,
so a future refactor that reaches for one fails loudly at the call site
instead of silently running a full parse inside a component that promises
never to. The method names below are the public API of
``mineru_vl_utils.MinerUClient`` (verified against version 2.0.7).
"""

from __future__ import annotations

from typing import Any

# The layout-only entry points, left untouched.
LAYOUT_METHODS = frozenset({
    "layout_detect",
    "batch_layout_detect",
    "aio_layout_detect",
    "aio_batch_layout_detect",
})

# Content recognition and full document parsing: the paths this worker must
# never invoke. Everything public on MinerUClient that is not a layout method,
# an accessor, or lifecycle management.
FULL_PARSING_METHODS = frozenset({
    "content_extract",
    "aio_content_extract",
    "batch_content_extract",
    "aio_batch_content_extract",
    "two_step_extract",
    "aio_two_step_extract",
    "batch_two_step_extract",
    "aio_batch_two_step_extract",
    "concurrent_two_step_extract",
    "aio_concurrent_two_step_extract",
    "stepping_two_step_extract",
    "aio_stepping_two_step_extract",
    "extract_with_layout",
    "aio_extract_with_layout",
    "batch_extract_with_layout",
    "aio_batch_extract_with_layout",
})


class FullParsingBlockedError(RuntimeError):
    """Raised when a guarded full-parsing entry point is invoked."""


def _blocked_method(name: str):
    def _raise(*_args: Any, **_kwargs: Any) -> Any:
        raise FullParsingBlockedError(
            f"The StructaDoc layout worker called the full-parsing SDK entry "
            f"point '{name}', which its contract forbids (ADR-0013). This is a "
            f"worker bug; report it with the request identifier.")

    return _raise


def install_full_parsing_guard(client: Any) -> list[str]:
    """Replace every full-parsing method on ``client`` with a raising stub.

    Returns the names that were actually found and replaced, so callers and
    tests can assert the guard is in force for the SDK version in use. A
    method that a future SDK renames or removes is simply absent from the
    result; the guard never widens what is allowed.
    """
    guarded: list[str] = []
    for name in sorted(FULL_PARSING_METHODS):
        if hasattr(client, name):
            setattr(client, name, _blocked_method(name))
            guarded.append(name)
    return guarded

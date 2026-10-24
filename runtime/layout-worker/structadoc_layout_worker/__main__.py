"""Process entry point: ``python -m structadoc_layout_worker``.

SIGTERM starts a graceful shutdown: new layout requests answer 503, and
in-flight requests are awaited. The honest limit, stated here as it is in the
deployment guide: a llama.cpp evaluation that is already running cannot
always be interrupted immediately, so an in-flight request may run to the end
of its evaluation before the process exits; a supervisor with a kill timeout
shorter than the worst evaluation will cut it off.
"""

from __future__ import annotations

import logging
import sys

import uvicorn

from .app import create_app
from .config import ConfigurationError, WorkerConfig
from .engine import EngineUnavailableError, build_engine


def silence_sdk_request_logging() -> None:
    """Stop the SDK's logger from writing request data to the log stream.

    ``mineru_vl_utils`` logs through loguru, not stdlib logging. Importing
    loguru installs a default stderr sink at DEBUG level, so the raw model
    output for each request (``Layout raw output: ...``) is printed whatever
    this application's stdlib level is — request data in logs, which
    ADR-0013 (point 5) and the deployment guide's retention statement forbid.
    The default sink is removed before the engine is built and replaced with
    one at WARNING: operational SDK warnings stay visible, request data does
    not reach the log stream. Loguru is absent in the weight-free
    environment (it arrives with the SDK); there is nothing to silence then.
    """
    try:
        from loguru import logger
    except ImportError:
        return
    logger.remove()
    logger.add(sys.stderr, level="WARNING")


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )
    silence_sdk_request_logging()

    try:
        config = WorkerConfig.from_env()
    except ConfigurationError as error:
        print(f"configuration error: {error}", file=sys.stderr)
        return 2

    # The engine loads before the server accepts a single connection, and a
    # model that will not load stops the process: an operator starting the
    # worker gets the failure at startup, not a health probe later.
    try:
        engine = build_engine(config)
    except EngineUnavailableError as error:
        print(f"engine error: {error}", file=sys.stderr)
        return 3

    app = create_app(config, engine=engine)

    server_config = uvicorn.Config(
        app,
        host=config.host,
        port=config.port,
        access_log=False,
        # No shutdown timeout: in-flight evaluations are awaited rather than
        # cancelled half-way; see the module docstring and the deployment guide.
        timeout_graceful_shutdown=None,
    )
    server = uvicorn.Server(server_config)

    original_handle_exit = server.handle_exit

    def handle_exit(sig, frame):  # noqa: ANN001 - uvicorn's handler signature
        app.state.worker.shutting_down = True
        original_handle_exit(sig, frame)

    server.handle_exit = handle_exit
    server.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

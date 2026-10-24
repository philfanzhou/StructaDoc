"""The optional StructaDoc layout inference worker (ADR-0013, point 4).

A small ASGI service that runs the official MinerU SDK's layout-only inference
path (``layout_detect``) in process and serves exactly the wire protocol the
merged ``MinerULayoutDetectionProvider`` adapter accepts. It never performs
content recognition or full document parsing and never persists request data.
"""

__version__ = "0.1.0"

"""Protocol conformance: the exact wire shape the merged adapter accepts.

The adapter rejects unknown or duplicate properties, missing members, wrong
types, out-of-range coordinates, labels outside ``[a-z0-9_-]{1,32}``, more
than 4096 regions, and bodies over 1 MiB. These tests hold the worker to
exactly that shape from the producing side.
"""

from __future__ import annotations

import json

import httpx
import pytest

from structadoc_layout_worker.app import LAYOUT_ROUTE
from structadoc_layout_worker.normalize import map_label, normalize_box

from conftest import encode_request, make_jpeg_bytes, make_png_bytes

EXPECTED_TOP_LEVEL = {"width", "height", "regions", "engine", "model", "version"}
EXPECTED_REGION_KEYS = {"label", "x0", "y0", "x1", "y1"}


@pytest.mark.anyio
async def test_success_response_is_exactly_the_known_property_set(client):
    response = await client.post(
        LAYOUT_ROUTE,
        json=encode_request(make_png_bytes(width=64, height=32)),
        headers={"content-type": "application/json"},
    )

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("application/json")
    payload = response.json()
    assert set(payload.keys()) == EXPECTED_TOP_LEVEL
    assert payload["width"] == 64
    assert payload["height"] == 32
    assert payload["engine"] == "stub"
    assert payload["model"] == "stub-layout-model"
    assert isinstance(payload["version"], str)
    for region in payload["regions"]:
        assert set(region.keys()) == EXPECTED_REGION_KEYS


@pytest.mark.anyio
async def test_regions_respect_coordinate_and_label_bounds(client):
    response = await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
    assert response.status_code == 200
    for region in response.json()["regions"]:
        assert region["label"] == region["label"].lower()
        assert len(region["label"]) <= 32
        assert all(
            0.0 <= region[name] <= 1.0 for name in ("x0", "y0", "x1", "y1"))
        assert region["x0"] <= region["x1"]
        assert region["y0"] <= region["y1"]


@pytest.mark.anyio
async def test_jpeg_media_type_is_accepted(client):
    response = await client.post(LAYOUT_ROUTE, json=encode_request(make_jpeg_bytes(), "image/jpeg"))
    assert response.status_code == 200
    assert response.json()["height"] == 48


@pytest.mark.anyio
async def test_response_body_stays_under_one_mebibyte(client):
    response = await client.post(LAYOUT_ROUTE, json=encode_request(make_png_bytes()))
    assert response.status_code == 200
    assert len(response.content) <= 1024 * 1024


@pytest.mark.anyio
async def test_unknown_request_property_is_rejected(client):
    body = encode_request(make_png_bytes())
    body["diagnostics"] = "extra"
    response = await client.post(LAYOUT_ROUTE, json=body)
    assert response.status_code == 422
    assert "diagnostics" not in response.text
    assert response.json()["error"] == "request-invalid"


@pytest.mark.anyio
async def test_missing_request_property_is_rejected(client):
    body = encode_request(make_png_bytes())
    del body["mediaType"]
    response = await client.post(LAYOUT_ROUTE, json=body)
    assert response.status_code == 422


@pytest.mark.anyio
async def test_duplicate_request_property_is_rejected(client):
    raw = (
        '{"mediaType": "image/png", "mediaType": "image/png", '
        '"image": "' + encode_request(make_png_bytes())["image"] + '"}'
    )
    response = await client.post(
        LAYOUT_ROUTE, content=raw.encode("utf-8"),
        headers={"content-type": "application/json"})
    assert response.status_code == 422


@pytest.mark.anyio
async def test_unsupported_media_type_value_is_rejected(client):
    response = await client.post(
        LAYOUT_ROUTE, json=encode_request(make_png_bytes(), "image/webp"))
    assert response.status_code == 422


@pytest.mark.anyio
async def test_declared_format_must_match_the_bytes(client):
    # Real PNG bytes declared as JPEG.
    response = await client.post(
        LAYOUT_ROUTE, json=encode_request(make_png_bytes(), "image/jpeg"))
    assert response.status_code == 422
    assert response.json()["error"] == "request-invalid"


@pytest.mark.anyio
async def test_invalid_base64_is_rejected_without_echoing_the_body(client):
    body = '{"mediaType": "image/png", "image": "!!not-base64!!"}'
    response = await client.post(
        LAYOUT_ROUTE, content=body.encode("utf-8"),
        headers={"content-type": "application/json"})
    assert response.status_code == 422
    assert "not-base64" not in response.text


@pytest.mark.anyio
async def test_non_json_content_type_is_refused(client):
    response = await client.post(
        LAYOUT_ROUTE,
        content=json.dumps(encode_request(make_png_bytes())).encode(),
        headers={"content-type": "text/plain"})
    assert response.status_code == 415


@pytest.mark.anyio
async def test_non_object_body_is_rejected(client):
    response = await client.post(
        LAYOUT_ROUTE, content=b"[1, 2, 3]",
        headers={"content-type": "application/json"})
    assert response.status_code == 422


def test_map_label_keeps_known_generic_labels():
    for label in ("text", "title", "doc_title", "table", "figure", "header",
                  "footer", "page_number", "image_block", "equation"):
        assert map_label(label) == label


def test_map_label_sends_unknown_categories_to_a_stable_generic_label():
    assert map_label("Weird New Category!") == "other"
    assert map_label("unknown") == "other"
    assert map_label("") == "other"
    assert map_label(None) == "other"
    assert map_label("x" * 40) == "other"


def test_normalize_box_clamps_and_orders():
    assert normalize_box(0.8, 0.9, 0.1, 0.2) == (0.1, 0.2, 0.8, 0.9)
    assert normalize_box(-0.5, 0.0, 1.7, 0.5) == (0.0, 0.0, 1.0, 0.5)

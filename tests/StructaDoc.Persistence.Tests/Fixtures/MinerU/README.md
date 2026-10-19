# MinerU Content List Fixture

`pipeline_content_list.json` follows the field structure of a real MinerU `pipeline` content list: field names, nesting, and value types. Every text value, image name, and number in it is synthetic; no third-party or private document content is included.

The structure was checked against MinerU's own Content List V1 renderer, `mineru/render/_internal/content_list/v1.py` in `opendatalab/MinerU` at commit `b7f3dd0` (release 4.0.12), plus the item types observed in a real MinerU Cloud `pipeline` run. Each item has a zero-based `page_idx` and an integer `bbox` on a 0–1000 scale.

`MinerUResultNormalizerTests` checks that every field in this fixture is either read by the normalizer (`MinerUResultNormalizer.RecognizedContentListFields`) or listed in the test's ignore list. When refreshing the fixture from newer MinerU output, a new or renamed field fails that test until it is mapped or deliberately ignored.

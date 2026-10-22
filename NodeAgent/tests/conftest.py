import json
import pathlib

import pytest

ROOT = pathlib.Path(__file__).resolve().parents[2]


@pytest.fixture(scope="session")
def vectors():
    return json.loads((ROOT / "WireProtocol" / "test-vectors" / "gsan.json").read_text())


@pytest.fixture(scope="session")
def radar_sample():
    return json.loads((pathlib.Path(__file__).parent / "fixtures" / "radar_target_sample.json").read_text())

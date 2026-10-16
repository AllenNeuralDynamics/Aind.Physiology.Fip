import json
import logging
from datetime import datetime
from pathlib import Path
from typing import Any

import pandas as pd
from aind_behavior_services.common import Circle, Point2f
from contraqctor.contract import DataStream

from aind_physiology_fip.data_mappers import ProtoAcquisitionMapper
from aind_physiology_fip.rig import RoiSettings


class MockCsv(DataStream[pd.DataFrame, Any]):
    _inner_data: pd.DataFrame

    def _reader(self, params: Any) -> pd.DataFrame:
        return self._inner_data


def _make_epoch(root: Path, name: str, regions: RoiSettings | None) -> Path:
    epoch = root / name
    (epoch / "SoftwareEvents").mkdir(parents=True)
    for file, ts in (
        ("StartSessionTime.json", "2025-07-18T19:03:19+00:00"),
        ("EndSessionTime.json", "2025-07-18T19:03:21+00:00"),
    ):
        (epoch / "SoftwareEvents" / file).write_text(json.dumps({"data": ts}) + "\n", encoding="utf-8")
    if regions is not None:
        (epoch / "regions.json").write_text(regions.model_dump_json(), encoding="utf-8")
    return epoch


def test_time_extraction_default():
    """Start and end times are read from the first and last CpuTime of the camera metadata."""
    data_stream = MockCsv(
        "camera_green_iso_metadata",
        reader_params=object(),
        description="Mock CSV data stream for testing.",
    )
    data_stream._inner_data = pd.DataFrame(
        {
            "CpuTime": [
                "2025-07-18T19:03:19.000Z",
                "2025-07-18T19:03:20.000Z",
                "2025-07-18T19:03:21.000Z",
            ],
            "SomeOtherData": [1, 2, 3],
        }
    )

    start_utc, end_utc = ProtoAcquisitionMapper._extract_from_df(data_stream.read())
    assert start_utc == datetime.fromisoformat("2025-07-18T19:03:19Z")
    assert end_utc == datetime.fromisoformat("2025-07-18T19:03:21+00:00")


def test_regions_are_extracted_from_regions_json(tmp_path):
    """An epoch's id, regions and start/end times are extracted from regions.json and the session events."""
    regions = RoiSettings(camera_green_iso_roi=[Circle(center=Point2f(x=10, y=20), radius=5)])
    epoch = _make_epoch(tmp_path, "fip_epoch", regions)
    streams = ProtoAcquisitionMapper._extract_start_end_times([epoch])

    assert len(streams) == 1
    assert streams[0].id == "fip_epoch"
    assert streams[0].regions == regions
    assert streams[0].start_time == datetime.fromisoformat("2025-07-18T19:03:19+00:00")
    assert streams[0].end_time == datetime.fromisoformat("2025-07-18T19:03:21+00:00")


def test_epoch_without_regions_is_skipped(tmp_path, caplog):
    """An epoch lacking regions.json is skipped with a warning instead of failing the whole mapping."""
    good = _make_epoch(tmp_path, "fip_good", RoiSettings())
    bad = _make_epoch(tmp_path, "fip_bad", None)
    # tests/__init__.py disables logging globally; re-enable it to assert on the warning
    logging.disable(logging.NOTSET)
    try:
        with caplog.at_level(logging.WARNING, logger="aind_physiology_fip.data_mappers._acquisition"):
            streams = ProtoAcquisitionMapper._extract_start_end_times([good, bad])
    finally:
        logging.disable(logging.CRITICAL)

    assert [s.id for s in streams] == ["fip_good"]
    assert any(r.levelno == logging.WARNING for r in caplog.records)

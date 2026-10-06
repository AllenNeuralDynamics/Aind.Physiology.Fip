import json
import logging
import sys
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from typing import Any

import pandas as pd
from aind_behavior_services.common import Circle, Point2f
from contraqctor.contract import DataStream

from aind_physiology_fip.data_mappers import ProtoAcquisitionMapper
from aind_physiology_fip.rig import RoiSettings

sys.path.append(".")


class MockCsv(DataStream[pd.DataFrame, Any]):
    _inner_data: pd.DataFrame

    def _reader(self, params: Any) -> pd.DataFrame:
        return self._inner_data


class TestAcquisitionMapper(unittest.TestCase):
    def test_time_extraction_default(self):
        _data_stream = MockCsv(
            "camera_green_iso_metadata",
            reader_params=object(),
            description="Mock CSV data stream for testing.",
        )
        _data_stream._inner_data = pd.DataFrame(
            {
                "CpuTime": [
                    "2025-07-18T19:03:19.000Z",
                    "2025-07-18T19:03:20.000Z",
                    "2025-07-18T19:03:21.000Z",
                ],
                "SomeOtherData": [1, 2, 3],
            }
        )

        start_utc, end_utc = ProtoAcquisitionMapper._extract_from_df(_data_stream.read())
        self.assertEqual(start_utc, datetime.fromisoformat("2025-07-18T19:03:19Z"))
        self.assertEqual(end_utc, datetime.fromisoformat("2025-07-18T19:03:21+00:00"))


class TestExtractStartEndTimes(unittest.TestCase):
    @staticmethod
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

    def test_regions_are_extracted_from_regions_json(self):
        regions = RoiSettings(camera_green_iso_roi=[Circle(center=Point2f(x=10, y=20), radius=5)])
        with tempfile.TemporaryDirectory() as tmp:
            epoch = self._make_epoch(Path(tmp), "fip_epoch", regions)
            streams = ProtoAcquisitionMapper._extract_start_end_times([epoch])

        self.assertEqual(len(streams), 1)
        self.assertEqual(streams[0].id, "fip_epoch")
        self.assertEqual(streams[0].regions, regions)
        self.assertEqual(streams[0].start_time, datetime.fromisoformat("2025-07-18T19:03:19+00:00"))
        self.assertEqual(streams[0].end_time, datetime.fromisoformat("2025-07-18T19:03:21+00:00"))

    def test_epoch_without_regions_is_skipped(self):
        with tempfile.TemporaryDirectory() as tmp:
            good = self._make_epoch(Path(tmp), "fip_good", RoiSettings())
            bad = self._make_epoch(Path(tmp), "fip_bad", None)
            # tests/__init__.py disables logging globally; re-enable it to assert on the warning
            logging.disable(logging.NOTSET)
            self.addCleanup(logging.disable, logging.CRITICAL)
            with self.assertLogs("aind_physiology_fip.data_mappers._acquisition", level="WARNING"):
                streams = ProtoAcquisitionMapper._extract_start_end_times([good, bad])

        self.assertEqual([s.id for s in streams], ["fip_good"])


if __name__ == "__main__":
    unittest.main()

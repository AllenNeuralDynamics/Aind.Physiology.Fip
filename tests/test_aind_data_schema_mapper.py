import json
import sys
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from typing import Any
from unittest.mock import MagicMock, patch

import pandas as pd
from aind_data_schema.core import instrument
from contraqctor.contract import DataStream

from aind_physiology_fip.data_mappers import ProtoAcquisitionMapper
from aind_physiology_fip.data_mappers._rig import AindInstrumentDataMapper

sys.path.append(".")
from examples.example import mock_rig


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


class TestAindInstrumentMapper(unittest.TestCase):
    """Tests aind schema instrument generation"""

    def setUp(self):
        """Set up for tests"""
        self.temp_dir = tempfile.TemporaryDirectory()
        self.data_path = Path(self.temp_dir.name)

        logs_dir = self.data_path / "fib" / "Logs"
        logs_dir.mkdir(parents=True, exist_ok=True)

        rig_input_path = logs_dir / "rig_input.json"
        with open(rig_input_path, "w", encoding="utf-8") as f:
            json.dump(mock_rig().model_dump(mode="json"), f, indent=2)

        self.rig_mapper = AindInstrumentDataMapper(data_path=rig_input_path.parent)

    def tearDown(self):
        """Cleanup"""
        self.temp_dir.cleanup()

    @patch("aind_physiology_fip.data_mappers._rig.AindInstrumentDataMapper._map")
    def test_rig_mock_map(self, mock_map):
        """Tests mock rig mapping"""
        mock_map.return_value = MagicMock()
        result = self.rig_mapper.map()
        self.assertIsNotNone(result)

    def test_rig_map(self):
        """Tests rig mapping"""
        mapped = self.rig_mapper.map()
        self.assertIsNotNone(mapped)

    def test_rig_round_trip(self):
        """Tests rig mapping validated with aind schema"""
        mapped = self.rig_mapper.map()
        assert mapped is not None
        instrument.Instrument.model_validate_json(mapped.model_dump_json())


if __name__ == "__main__":
    unittest.main()

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

from aind_physiology_fip.data_mappers import INSTRUMENT_FILE_NAME, DataMapperCli, ProtoAcquisitionMapper
from aind_physiology_fip.data_mappers._instrument import AindInstrumentDataMapper

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

        self.epoch = self.data_path / "fib" / "fip_2025-10-30T162001"
        logs_dir = self.epoch / "Logs"
        logs_dir.mkdir(parents=True, exist_ok=True)

        rig_input_path = logs_dir / "rig_input.json"
        with open(rig_input_path, "w", encoding="utf-8") as f:
            json.dump(mock_rig().model_dump(mode="json"), f, indent=2)

        self._write_frame_metadata("green", 200, 200, "U16")
        self._write_frame_metadata("iso", 200, 200, "U16")
        self._write_frame_metadata("red", 200, 200, "U16")

        self.rig_mapper = AindInstrumentDataMapper(data_path=self.data_path)

    def _write_frame_metadata(self, channel: str, width: int, height: int, depth: str) -> None:
        """Write a channel metadata file of the shape FipWriter.cs emits."""
        path = self.epoch / f"{channel}_metadata.json"
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"Width": width, "Height": height, "Depth": depth}, f)

    def tearDown(self):
        """Cleanup"""
        self.temp_dir.cleanup()

    @patch("aind_physiology_fip.data_mappers._instrument.AindInstrumentDataMapper._map")
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

    def test_detector_reads_frame_metadata(self):
        """Detector crop and bit depth come from the channel metadata, not the defaults"""
        self._write_frame_metadata("green", 128, 64, "U8")
        self._write_frame_metadata("iso", 128, 64, "U8")
        detectors = {d.name: d for d in self.rig_mapper.map().components if d.object_type == "Detector"}

        green = detectors["Green CMOS"]
        self.assertEqual((green.crop_width, green.crop_height, green.bit_depth), (128, 64, 8))
        red = detectors["Red CMOS"]
        self.assertEqual((red.crop_width, red.crop_height, red.bit_depth), (200, 200, 16))

    def test_green_iso_metadata_must_agree(self):
        """The green/iso camera is one detector, so its two channels may not disagree"""
        self._write_frame_metadata("iso", 128, 64, "U8")
        with self.assertRaises(ValueError) as ctx:
            self.rig_mapper.map()
        self.assertIn("disagrees", str(ctx.exception))

    def test_missing_frame_metadata_raises(self):
        """Without frame metadata there is no crop or bit depth to report, so refuse to guess"""
        for channel in ("green", "iso", "red"):
            (self.epoch / f"{channel}_metadata.json").unlink()
        with self.assertRaises(FileNotFoundError) as ctx:
            self.rig_mapper.map()
        self.assertIn("green_metadata.json", str(ctx.exception))

    @patch("aind_physiology_fip.data_mappers.ProtoAcquisitionMapper")
    def test_cli_writes_instrument_file(self, mock_acquisition_mapper):
        """The CLI writes the instrument next to the acquisition metadata it already emitted"""
        mock_acquisition_mapper.return_value.map.return_value.model_dump_json.return_value = "{}"

        DataMapperCli(data_path=self.data_path).cli_cmd()

        written = self.data_path / INSTRUMENT_FILE_NAME
        self.assertTrue(written.is_file())
        mapped = instrument.Instrument.model_validate_json(written.read_text(encoding="utf-8"))
        self.assertEqual(mapped.instrument_id, mock_rig().rig_name)

    def test_partial_frame_metadata_raises(self):
        """One half of the green/iso pair is not enough to check the two agree"""
        (self.epoch / "iso_metadata.json").unlink()
        with self.assertRaises(FileNotFoundError) as ctx:
            self.rig_mapper.map()
        self.assertIn("iso_metadata.json", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()

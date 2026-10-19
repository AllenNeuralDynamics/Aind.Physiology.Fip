import json
import logging
import sys
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from typing import Any
from unittest.mock import MagicMock, patch

import pandas as pd
from aind_behavior_services.common import Circle, Point2f
from aind_data_schema.core import instrument
from contraqctor.contract import DataStream

from aind_physiology_fip.data_mappers import INSTRUMENT_FILE_NAME, DataMapperCli, ProtoAcquisitionMapper
from aind_physiology_fip.data_mappers._instrument import AindInstrumentDataMapper
from aind_physiology_fip.rig import RoiSettings

sys.path.append(".")
from examples.example import mock_rig


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

    def test_trigger_connections(self):
        """The cuttlefish board is wired out to each LED and camera it triggers"""
        mapped = self.rig_mapper.map()
        triggers = {
            (c.source_port, c.target_device)
            for c in mapped.connections
            if c.source_device == "cuTTLefishFip" and c.source_port.startswith("IO")
        }
        self.assertEqual(
            triggers,
            {
                ("IO3", "415nm LED"),
                ("IO2", "470nm LED"),
                ("IO4", "565nm LED"),
                ("IO0", "Green CMOS"),
                ("IO1", "Red CMOS"),
            },
        )

    def test_shared_camera_port_is_not_duplicated(self):
        """The uv and blue tasks name the same camera line, which is one connection not two"""
        mapped = self.rig_mapper.map()
        green = [c for c in mapped.connections if c.target_device == "Green CMOS"]
        self.assertEqual(len(green), 1)

    def test_calibrations(self):
        """Light sources carrying a power look-up table become PowerCalibrations"""
        mapped = self.rig_mapper.map()
        by_device = {c.device_name: c for c in mapped.calibrations}

        # The mock rig calibrates blue and lime, but leaves uv uncalibrated.
        self.assertEqual(set(by_device), {"470nm LED", "565nm LED"})

        blue = by_device["470nm LED"]
        self.assertEqual(blue.output, [0.0, 10.0, 20.0])
        self.assertEqual(blue.output_unit, "milliwatt")
        # Duty cycle is stored as a fraction but recorded as a percentage strength.
        self.assertEqual(blue.input, [0.0, 10.0, 20.0])
        self.assertEqual(blue.input_unit, "percent")

    def test_calibration_date_matches_modification_date(self):
        """Calibrations are stamped with the same moment the instrument is mapped"""
        mapped = self.rig_mapper.map()
        for calibration in mapped.calibrations:
            self.assertEqual(calibration.calibration_date.date(), mapped.modification_date)
            self.assertIsNotNone(calibration.calibration_date.tzinfo)

    def test_objective_serial_number(self):
        """The objective serial is supplied per instrument rather than hardcoded"""
        mapper = AindInstrumentDataMapper(data_path=self.data_path, objective_serial_number="12812388")
        objective = next(c for c in mapper.map().components if c.object_type == "Objective")
        self.assertEqual(objective.serial_number, "12812388")

    def test_objective_serial_number_unset(self):
        """An instrument that was never configured records no serial rather than a wrong one"""
        objective = next(c for c in self.rig_mapper.map().components if c.object_type == "Objective")
        self.assertIsNone(objective.serial_number)

    def test_partial_frame_metadata_raises(self):
        """One half of the green/iso pair is not enough to check the two agree"""
        (self.epoch / "iso_metadata.json").unlink()
        with self.assertRaises(FileNotFoundError) as ctx:
            self.rig_mapper.map()
        self.assertIn("iso_metadata.json", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()

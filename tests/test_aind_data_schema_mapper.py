import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from aind_data_schema.core import instrument
from aind_physiology_fip_data_mappers import INSTRUMENT_FILE_NAME, DataMapperCli
from aind_physiology_fip_data_mappers._instrument import AindInstrumentDataMapper

sys.path.append(".")
from examples.example import mock_rig


class TestAindInstrumentMapper(unittest.TestCase):
    """Tests aind schema instrument generation"""

    CLOCK_GENERATOR_NAME = "harp_clock_generator"

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

        self.rig_mapper = AindInstrumentDataMapper(
            data_path=self.data_path, clock_generator_name=self.CLOCK_GENERATOR_NAME
        )

    def _write_frame_metadata(self, channel: str, width: int, height: int, depth: str) -> None:
        """Write a channel metadata file of the shape FipWriter.cs emits."""
        path = self.epoch / f"{channel}_metadata.json"
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"Width": width, "Height": height, "Depth": depth}, f)

    def tearDown(self):
        """Cleanup"""
        self.temp_dir.cleanup()

    @patch("aind_physiology_fip_data_mappers._instrument.AindInstrumentDataMapper._map")
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
        """Detector crop comes from the channel metadata, not a default"""
        self._write_frame_metadata("green", 128, 64, "U8")
        self._write_frame_metadata("iso", 128, 64, "U8")
        detectors = {d.name: d for d in self.rig_mapper.map().components if d.object_type == "Detector"}

        green = detectors["Green CMOS"]
        self.assertEqual((green.crop_width, green.crop_height), (128, 64))
        red = detectors["Red CMOS"]
        self.assertEqual((red.crop_width, red.crop_height), (200, 200))

    def test_detector_bit_depth_is_the_sensors(self):
        """Bit depth is the sensor's 10-bit ADC, not the 8- or 16-bit container the frames are saved in"""
        self._write_frame_metadata("green", 200, 200, "U8")
        self._write_frame_metadata("iso", 200, 200, "U8")
        detectors = [d for d in self.rig_mapper.map().components if d.object_type == "Detector"]
        self.assertEqual({d.bit_depth for d in detectors}, {10})

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

    @patch("aind_physiology_fip_data_mappers.AindAcquisitionDataMapper")
    def test_cli_writes_instrument_file(self, mock_acquisition_mapper):
        """The CLI writes the instrument into the session directory"""

        DataMapperCli(data_path=self.data_path, clock_generator_name=self.CLOCK_GENERATOR_NAME).cli_cmd()

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
        green = [
            c for c in mapped.connections if c.source_device == "cuTTLefishFip" and c.target_device == "Green CMOS"
        ]
        self.assertEqual(len(green), 1)

    def test_calibrations(self):
        """Light sources carrying a power look-up table become PowerCalibrations"""
        mapped = self.rig_mapper.map()
        by_device = {c.device_name: c for c in mapped.calibrations}

        # The mock rig calibrates blue and lime, but leaves uv uncalibrated.
        self.assertEqual(set(by_device), {"470nm LED", "565nm LED"})

        blue = by_device["470nm LED"]
        self.assertEqual(blue.output, [0.0, 10.0, 20.0])
        self.assertEqual(blue.output_unit, "microwatt")
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
        mapper = AindInstrumentDataMapper(
            data_path=self.data_path,
            clock_generator_name=self.CLOCK_GENERATOR_NAME,
            objective_serial_number="12812388",
        )
        objective = next(c for c in mapper.map().components if c.object_type == "Objective")
        self.assertEqual(objective.serial_number, "12812388")

    def test_objective_serial_number_unset(self):
        """An instrument that was never configured records no serial rather than a wrong one"""
        objective = next(c for c in self.rig_mapper.map().components if c.object_type == "Objective")
        self.assertIsNone(objective.serial_number)

    def test_clock_generator_name_is_supplied(self):
        """The clock generator takes the name the behavior side uses, wherever it is referenced"""
        mapper = AindInstrumentDataMapper(data_path=self.data_path, clock_generator_name="WhiteRabbit")
        mapped = mapper.map()

        clocks = [c for c in mapped.components if getattr(c, "is_clock_generator", False)]
        self.assertEqual([c.name for c in clocks], ["WhiteRabbit"])
        self.assertTrue(
            any(c.source_device == "WhiteRabbit" and c.target_device == "cuTTLefishFip" for c in mapped.connections)
        )
        self.assertFalse(any("harp_clock_generator" in (c.source_device, c.target_device) for c in mapped.connections))

    def test_clock_generator_name_defaults_to_the_behavior_tasks(self):
        """Left unset, the clock generator takes the name the behavior tasks use for it"""
        mapped = AindInstrumentDataMapper(data_path=self.data_path).map()
        clocks = [c.name for c in mapped.components if getattr(c, "is_clock_generator", False)]
        self.assertEqual(clocks, ["harp_clock_generator"])
        self.assertEqual(DataMapperCli(data_path=self.data_path).clock_generator_name, "harp_clock_generator")

    def _add_epoch(self, name: str, rig) -> Path:
        """Add another FIP epoch to the session, logging the given rig input."""
        epoch = self.data_path / "fib" / name
        (epoch / "Logs").mkdir(parents=True, exist_ok=True)
        with open(epoch / "Logs" / "rig_input.json", "w", encoding="utf-8") as f:
            json.dump(rig.model_dump(mode="json"), f, indent=2)
        return epoch

    def test_identical_epochs_map(self):
        """Several epochs logging the same rig input describe one instrument, so mapping succeeds"""
        self._add_epoch("fip_2025-10-30T170000", mock_rig())
        mapped = self.rig_mapper.map()
        self.assertEqual(mapped.instrument_id, mock_rig().rig_name)

    def test_differing_epochs_raise(self):
        """Epochs whose rig inputs disagree cannot be reconciled into one instrument"""
        other = mock_rig()
        other.camera_red.serial_number = "not-the-same-camera"
        self._add_epoch("fip_2025-10-30T170000", other)
        with self.assertRaises(ValueError) as ctx:
            self.rig_mapper.map()
        self.assertIn("fip_2025-10-30T170000", str(ctx.exception))

    def test_epoch_without_rig_input_is_skipped(self):
        """An epoch that never logged a rig input is not a disagreement"""
        (self.data_path / "fib" / "fip_2025-10-30T170000").mkdir(parents=True)
        mapped = self.rig_mapper.map()
        self.assertEqual(mapped.instrument_id, mock_rig().rig_name)

    def test_partial_frame_metadata_raises(self):
        """One half of the green/iso pair is not enough to check the two agree"""
        (self.epoch / "iso_metadata.json").unlink()
        with self.assertRaises(FileNotFoundError) as ctx:
            self.rig_mapper.map()
        self.assertIn("iso_metadata.json", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()

import sys
import unittest
from datetime import datetime
from pathlib import Path

from aind_data_schema.core import acquisition, instrument
from aind_data_schema.utils import compatibility_check
from git import Repo

from aind_physiology_fip.data_mappers import AcquisitionMapper, InstrumentMapper

sys.path.append(".")
from examples.example import mock_rig, mock_session


class TestAcquisitionMapper(unittest.TestCase):
    def setUp(self):
        self.session = mock_session
        self.rig = mock_rig
        self.repository = Repo(Path("./"))
        self.session_end_time = datetime.now()
        self.session_directory = None

        self.mapper = AcquisitionMapper()

    def test_map(self):
        mapped = self.mapper.map()
        self.assertIsNotNone(mapped)

    def test_round_trip(self):
        mapped = self.mapper.map()
        assert mapped is not None
        acquisition.Acquisition.model_validate_json(mapped.model_dump_json())


class TestInstrumentMapper(unittest.TestCase):
    def setUp(self):
        self.rig = mock_rig()
        self.mapper = InstrumentMapper(
            rig=self.rig,
        )

    def test_map(self):
        mapped = self.mapper.map()
        self.assertIsNotNone(mapped)

    def test_round_trip(self):
        mapped = self.mapper.map()
        assert mapped is not None
        instrument.Instrument.model_validate_json(mapped.model_dump_json())


class TestInstrumentAcquisitionCompatibility(unittest.TestCase):
    def setUp(self):
        self.rig = mock_rig()
        self.session = mock_session()
        self.rig_mapper = InstrumentMapper(
            rig=self.rig,
        )
        self.session_mapper = AcquisitionMapper()

    def test_compatibility(self):
        session_mapped = self.session_mapper.map()
        assert session_mapped is not None
        rig_mapped = self.rig_mapper.map()
        assert rig_mapped is not None
        compatibility_check.InstrumentAcquisitionCompatibility(
            instrument=rig_mapped, acquisition=session_mapped
        ).run_compatibility_check()


if __name__ == "__main__":
    unittest.main()

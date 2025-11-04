import sys
import unittest
from datetime import datetime
from typing import Any

import pandas as pd
from contraqctor.contract import DataStream

from aind_physiology_fip.data_mappers import ProtoAcquisitionMapper

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


if __name__ == "__main__":
    unittest.main()

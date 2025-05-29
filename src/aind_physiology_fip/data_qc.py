from pathlib import Path

from contraqctor.qc import Runner, Suite
from contraqctor.qc.contract import ContractTestSuite
from contraqctor.qc.csv import CsvTestSuite
import contraqctor.contract as contract
import typing as t

import numpy as np


from aind_physiology_fip.data_contract import dataset, AindPhysioFipRig


class FipCameraChannelTestSuite(Suite):
    _expected_columns = {"ReferenceTime", "CameraFrameNumber", "CameraFrameTime"}

    def __init__(
        self,
        channel_data: contract.csv.Csv,
        frame_stride: int,
        *,
        clock_jitter_s: float = 1e-4,
        expected_fps: t.Optional[float] = None,
    ) -> None:
        self.channel_data = channel_data
        self.frame_stride = frame_stride
        self.clock_jitter_s = clock_jitter_s
        self.expected_fps = expected_fps

    def test_check_dropped_frames(self):
        """
        Check if there are dropped frames in the metadata DataFrame.
        """
        metadata = (self.channel_data.data[list(self._expected_columns - {"ReferenceTime"})]).copy()
        metadata.loc[:, "ReferenceTime"] = metadata.index.values
        diff_metadata = metadata.diff()

        # Convert CameraFrameTime to seconds
        diff_metadata["CameraFrameTime"] = diff_metadata["CameraFrameTime"] * 1e-9

        if not all(diff_metadata["CameraFrameNumber"].dropna() == self.frame_stride):
            return self.fail_test(
                None,
                f"Detected {sum((diff_metadata['CameraFrameNumber'].dropna() - self.frame_stride) > 0)} dropped frames metadata.",
            )

        inter_clock_diff = diff_metadata["CameraFrameTime"] - diff_metadata["ReferenceTime"]
        if not all(inter_clock_diff.dropna() < self.clock_jitter_s):
            return self.fail_test(
                None,
                f"Detected a difference between CameraFrameTime and ReferenceTime greater than the expected threshold: {self.clock_jitter_s} s.",
            )
        return self.pass_test(None, "No dropped frames detected in metadata.")

    def test_match_expected_fps(self):
        """
        Check if the frames per second (FPS) of the video metadata matches the expected FPS."""
        if self.expected_fps is None:
            return self.skip_test("No expected FPS provided, skipping test.")
        period = np.diff(self.channel_data.data.index.values)
        if np.std(period) > 1e-4:
            return self.fail_test(None, f"High std in frame period detected: {np.std(period)}")
        if abs(_mean := np.mean(period) - (_expected := (1.0 / self.expected_fps))) > (_expected * 0.01):
            return self.fail_test(None, f"Mean frame period ({_mean}) is different than expected: {_expected}")

        return self.pass_test(None, f"Mean frame period ({_mean}) is within expected range: {_expected}")


runner = Runner()

runner.add_suite(ContractTestSuite(dataset.load_all()), "Contract tests")


for data_stream in dataset.iter_all():
    if isinstance(data_stream, contract.csv.Csv):
        runner.add_suite(CsvTestSuite(data_stream), "Csv tests")


rig = t.cast(AindPhysioFipRig, dataset["rig_input"])  # todo auto detect fps

runner.add_suite(FipCameraChannelTestSuite(dataset["green"], frame_stride=2, expected_fps=20), "Camera tests")
runner.add_suite(FipCameraChannelTestSuite(dataset["iso"], frame_stride=2, expected_fps=20), "Camera tests")
runner.add_suite(FipCameraChannelTestSuite(dataset["red"], frame_stride=1, expected_fps=20), "Camera tests")


runner.run_all_with_progress()

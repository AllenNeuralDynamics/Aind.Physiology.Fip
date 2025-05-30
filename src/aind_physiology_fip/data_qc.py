import secrets
import typing as t

import contraqctor.contract as contract
import matplotlib
import matplotlib.figure
import numpy as np
from contraqctor.qc import ContextExportableObj, Runner, Suite
from contraqctor.qc.contract import ContractTestSuite
from contraqctor.qc.csv import CsvTestSuite

from aind_physiology_fip.data_contract import AindPhysioFipRig, dataset
from aind_physiology_fip.data_qc_helpers import plot_sensor_floor


class FipChannelTestSuite(Suite):
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


class FipChannelSignalTestSuite(Suite):
    _reg_exp_ = r"Fiber_\d+"
    cmos_floor_limit = 265

    def __init__(
        self, color_channel: contract.csv.Csv, *, channel_name: t.Optional[t.Literal["green", "iso", "red"]] = None
    ) -> None:
        self.channel_name = channel_name or color_channel.name
        self.background_ch = color_channel.data["Background"]
        self.data = color_channel.data

    def test_sensor_floor(self):
        fig, sensor_floor = plot_sensor_floor(self.background_ch, self.channel_name)
        fig = ContextExportableObj.as_context(fig)

        if sensor_floor > self.cmos_floor_limit:
            return self.fail_test(
                sensor_floor,
                f"Sensor floor value {sensor_floor} exceeds the limit of {self.cmos_floor_limit}.",
                context=fig,
            )
        else:
            return self.pass_test(
                sensor_floor,
                f"Sensor floor value {sensor_floor} is within the acceptable range.",
                context=fig,
            )

    def test_has_nans(self):
        if (nan_count := self.data.isna().sum().sum()) > 0:
            return self.fail_test(None, f"Data contains {nan_count} NaNs.")
        return self.pass_test(None, "Data does not contain NaNs.")


class FipAcquisitionTestSuite(Suite):
    def __init__(self, dataset: contract.Dataset) -> None:
        self.dataset = dataset
        self.green_ch = t.cast(contract.csv.Csv, dataset["green"])
        self.iso_ch = t.cast(contract.csv.Csv, dataset["iso"])
        self.red_ch = t.cast(contract.csv.Csv, dataset["red"])

    def test_same_size_across_channels(self):
        green_size = self.green_ch.data.shape[0]
        iso_size = self.iso_ch.data.shape[0]
        red_size = self.red_ch.data.shape[0]

        if green_size != iso_size or green_size != red_size:
            return self.fail_test(
                None,
                f"Channel sizes do not match: GreenCh: {green_size}, IsoCh: {iso_size}, RedCh: {red_size}",
            )
        return self.pass_test(None, "All channels have the same number of frames and overal shape.")

    def test_is_data_longer_than_15_minutes(self):
        total_seconds = self.green_ch.data.index.values[-1] - self.green_ch.data.index.values[0]
        if total_seconds < 15 * 60:
            return self.fail_test(total_seconds, f"Data is shorter than 15 minutes: {total_seconds / 60:.2f} minutes.")
        return self.pass_test(total_seconds, f"Data is longer than 15 minutes: {total_seconds / 60:.2f} minutes.")


runner = Runner()

runner.add_suite(ContractTestSuite(dataset.load_all()), "Contract tests")

for data_stream in dataset.iter_all():
    if isinstance(data_stream, contract.csv.Csv):
        runner.add_suite(CsvTestSuite(data_stream), data_stream.name)

rig = t.cast(AindPhysioFipRig, dataset["rig_input"])  # todo auto detect fps

for color, stride in zip(
    ["green", "iso", "red"],
    [2, 2, 1],
):
    color_channel = t.cast(contract.csv.Csv, dataset[color])
    runner.add_suite(
        FipChannelTestSuite(color_channel, frame_stride=stride, expected_fps=20),
        color_channel.name,
    )
    runner.add_suite(FipChannelSignalTestSuite(color_channel), color_channel.name)

runner.add_suite(
    FipAcquisitionTestSuite(dataset),
    "Dataset tests",
)
results = runner.run_all_with_progress()

for group, group_results in results.items():
    for result in group_results:
        if isinstance(result.context, dict):
            asset = result.context.get("asset", None)
            if isinstance(asset, ContextExportableObj):
                if isinstance(asset.asset, matplotlib.figure.Figure):
                    asset.asset.savefig(f"{result.suite_name}_{result.test_name}_{secrets.token_hex(4)}.png")

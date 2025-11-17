import dataclasses
import datetime
import logging
import os
import sys
from pathlib import Path
from typing import List, Literal, Optional, Union, cast

import aind_behavior_services.rig as AbsRig
import git
import pydantic
from aind_behavior_services.session import AindBehaviorSessionModel
from aind_behavior_services.utils import get_fields_of_type
from aind_data_schema.components import configs, measurements
from aind_data_schema.core import acquisition
from aind_data_schema_models import units
from aind_data_schema_models.modalities import Modality
from clabe.apps import BonsaiAppSettings
from clabe.data_mapper import aind_data_schema as ads
from clabe.data_mapper import helpers as data_mapper_helpers
from clabe.ui import DefaultUIHelper
from pandas import DataFrame

from aind_physiology_fip import rig as fip_rig
from aind_physiology_fip.data_contract import dataset

from ._utils import _make_origin_coordinate_system

logger = logging.getLogger(__name__)


@dataclasses.dataclass
class _ChannelInformation:
    camera_name: str
    light_source_name: str
    camera: fip_rig.FipCamera
    task: fip_rig.FipTask
    excitation_wavelength_nm: int
    emission_wavelength_nm: int


class AindAcquisitionDataMapper(ads.AindDataSchemaSessionDataMapper):
    def __init__(
        self,
        session_directory: os.PathLike,
        session: AindBehaviorSessionModel,
        rig: fip_rig.AindPhysioFipRig,
        bonsai_app_settings: BonsaiAppSettings = BonsaiAppSettings(workflow=Path("./src/main.bonsai")),
        repository: Union[os.PathLike, git.Repo] = Path("."),
        *,
        ui_helper: Optional[DefaultUIHelper] = None,
    ):
        self.session_directory = session_directory
        self.session_model = session
        self.rig_model = rig
        self.repository = repository
        if isinstance(self.repository, os.PathLike | str):
            self.repository = git.Repo(Path(self.repository))
        self.bonsai_app = bonsai_app_settings
        self._mapped: Optional[acquisition.Acquisition] = None
        self._session_end_time: Optional[datetime.datetime] = None
        self._ui_helper = ui_helper or DefaultUIHelper()

    @property
    def session_end_time(self) -> datetime.datetime:
        if self._session_end_time is None:
            raise ValueError("Session end time is not set.")
        return self._session_end_time

    def session_schema(self):
        return self.mapped

    @property
    def session_name(self) -> str:
        if self.session_model.session_name is None:
            raise ValueError("Session name is not set in the session model.")
        return self.session_model.session_name

    @property
    def mapped(self) -> acquisition.Acquisition:
        if self._mapped is None:
            raise ValueError("Data has not been mapped yet.")
        return self._mapped

    def is_mapped(self) -> bool:
        return self.mapped is not None

    def map(self) -> acquisition.Acquisition:
        logger.info("Mapping aind-data-schema Acquisition.")
        try:
            self._mapped = self._map()
        except (pydantic.ValidationError, ValueError, IOError) as e:
            logger.error("Failed to map to aind-data-schema Acquisition. %s", e)
            raise e
        else:
            return self._mapped

    def _map(self) -> acquisition.Acquisition:
        epochs_time = self._get_start_end_times_per_epoch(self.session_directory)
        if len(epochs_time) == 0:
            raise ValueError("No valid FIP epochs found in the session directory.")
        min_start, max_end = (min([t[0] for t in epochs_time]), max([t[1] for t in epochs_time]))
        min_start = min_start.replace(tzinfo=datetime.timezone.utc)
        max_end = max_end.replace(tzinfo=datetime.timezone.utc)

        # Construct aind-data-schema session
        aind_data_schema_session = acquisition.Acquisition(
            subject_id=self.session_model.subject,
            instrument_id=self.rig_model.rig_name,
            acquisition_end_time=max_end,
            acquisition_start_time=min_start,
            experimenters=self.session_model.experimenter,
            acquisition_type=self.session_model.experiment or self._ui_helper.prompt_text("Enter experiment name: "),
            coordinate_system=_make_origin_coordinate_system(),
            data_streams=self._get_data_streams(epochs_time),
            calibrations=self._get_calibrations(),
            stimulus_epochs=[],
        )
        return aind_data_schema_session

    def _get_calibrations(self) -> List[acquisition.CALIBRATIONS]:
        calibrations: List[acquisition.CALIBRATIONS] = []
        light_sources = get_fields_of_type(self.rig_model, fip_rig.LightSource)
        for name, obj in light_sources:
            assert name is not None, "Light source name is not set."
            calibrations.append(self._calibration_from_light_source(name, obj))
        return calibrations

    def _calibration_from_light_source(
        self, device_name: str, light_source: fip_rig.LightSource
    ) -> measurements.PowerCalibration:
        if light_source.calibration is not None:
            # DutyCycle (0-1) to Power (mW)
            return measurements.PowerCalibration(
                calibration_date=self.session_model.date,
                device_name=device_name,
                input=[v * 100 for v in light_source.calibration.output.power_lut.keys()],
                output=list(light_source.calibration.output.power_lut.values()),
                input_unit=measurements.PowerUnit.PERCENT,
                output_unit=measurements.PowerUnit.MW,
            )
        else:
            logger.warning(f"Light source {device_name} does not have calibration data. Building unitless calibration.")
            return measurements.PowerCalibration(
                calibration_date=self.session_model.date,
                device_name=device_name,
                input=[0, 100],
                output=[0, 100],
                input_unit=measurements.PowerUnit.PERCENT,
                output_unit=measurements.PowerUnit.PERCENT,
            )

    @staticmethod
    def _include_device(device: AbsRig.Device) -> bool:
        return True

    def _get_data_streams(
        self, epochs_time: List[tuple[datetime.datetime, datetime.datetime]]
    ) -> List[acquisition.DataStream]:
        assert self.session_end_time is not None, "Session end time is not set."

        modalities: list[Modality] = [getattr(Modality, "FIB")]

        active_devices = [
            _device[0]
            for _device in get_fields_of_type(self.rig_model, AbsRig.Device, stop_recursion_on_type=True)
            if _device[0] is not None and self._include_device(_device[1])
        ]
        data_streams: list[acquisition.DataStream] = []
        for epoch in epochs_time:
            data_streams.append(
                acquisition.DataStream(
                    stream_start_time=epoch[0],
                    stream_end_time=epoch[1],
                    code=[self._get_bonsai_as_code(), self._get_python_as_code()],
                    active_devices=active_devices,
                    modalities=modalities,
                    configurations=self._get_cameras_config(),
                )
            )
        return data_streams

    def _get_cameras_config(self) -> List[acquisition.DetectorConfig]:
        def _map_camera(name: str) -> acquisition.DetectorConfig:
            return acquisition.DetectorConfig(
                device_name=name,
                exposure_time=-1,
                exposure_time_unit=units.TimeUnit.US,
                trigger_type=configs.TriggerType.EXTERNAL,
                compression=None,
            )

        cameras = get_fields_of_type(self.rig_model, fip_rig.FipCamera)
        assert all(name is not None for name, _ in cameras)
        return [_map_camera(name) for name in cameras]

    def _get_bonsai_as_code(self) -> acquisition.Code:
        bonsai_folder = Path(self.bonsai_app.executable).parent
        bonsai_env = data_mapper_helpers.snapshot_bonsai_environment(bonsai_folder / "bonsai.config")
        bonsai_version = bonsai_env.get("Bonsai", "unknown")
        assert isinstance(self.repository, git.Repo)

        return acquisition.Code(
            url=self.repository.remote().url,
            name="Aind.Behavior.VrForaging",
            version=self.repository.head.commit.hexsha,
            language="Bonsai",
            language_version=bonsai_version,
            run_script=Path(self.bonsai_app.workflow),
        )

    def _get_python_as_code(self) -> acquisition.Code:
        assert isinstance(self.repository, git.Repo)
        # python_env = data_mapper_helpers.snapshot_python_environment()
        v = sys.version_info
        semver = f"{v.major}.{v.minor}.{v.micro}"
        if v.releaselevel != "final":
            semver += f"-{v.releaselevel}.{v.serial}"
        return acquisition.Code(
            url=self.repository.remote().url,
            name="aind-behavior-vr-foraging",
            version=self.repository.head.commit.hexsha,
            language="Python",
            language_version=semver,
        )

    @classmethod
    def _get_start_end_times_per_epoch(
        cls, data_directory: os.PathLike
    ) -> list[tuple[datetime.datetime, datetime.datetime]]:
        epochs = list((Path(data_directory) / "fib").glob("fip_*"))
        data_streams = []
        _candidate_streams = [
            "camera_green_iso_metadata",
            "camera_red_metadata",
        ]
        for epoch in epochs:
            if not epoch.is_dir():
                continue
            try:
                this_epoch = dataset(root=epoch)
                for stream in _candidate_streams:
                    logger.debug(f"Checking for timing in stream: {stream}")
                    start_utc, end_utc = cls._extract_from_df(cast(DataFrame, this_epoch[stream].read()))
                    data_streams.append((start_utc, end_utc))
            except Exception as e:
                # Log warning but continue processing other epochs
                logger.warning(f"Failed to load FIP dataset at {epoch}: {e}")
                continue
        return data_streams

    @staticmethod
    def _extract_from_df(df: DataFrame) -> tuple[datetime.datetime, datetime.datetime]:
        if df is None or df.empty:
            raise ValueError("DataFrame is None or empty.")
        start_utc = datetime.datetime.fromisoformat(df["CpuTime"].iloc[0])
        end_utc = datetime.datetime.fromisoformat(df["CpuTime"].iloc[-1])
        return start_utc, end_utc

    def _create_channel(
        self, fiber_name: str, channel_name: Literal["iso", "green", "red"], intended_measurement: Optional[str]
    ) -> configs.Channel:
        camera_task = self.get_camera_task_from_channel_name(channel_name)
        return configs.Channel(
            channel_name=f"{fiber_name}_{channel_name}",
            intended_measurement=intended_measurement,
            detector=configs.DetectorConfig(
                device_name=camera_task.camera_name,
                exposure_time=camera_task.task.delta_1,
                exposure_time_unit=measurements.TimeUnit.US,
                trigger_type=configs.TriggerType.EXTERNAL,
            ),
            light_sources=[self._get_led_config_from_light_source(camera_task.light_source_name)],
            excitation_filters=[],
            emission_filters=[],
            emission_wavelength=camera_task.emission_wavelength_nm,
            emission_wavelength_unit=configs.SizeUnit.NM,
        )

    def _get_led_config_from_light_source(self, name: str) -> configs.LightEmittingDiodeConfig:
        light_source = getattr(self.rig_model, name, None)
        if light_source is None:
            raise ValueError(f"Light source {name} not found in rig model.")
        if not isinstance(light_source, fip_rig.LightSource):
            raise TypeError(f"Expected LightSource instance for {name}, got {type(light_source)}")

        return configs.LightEmittingDiodeConfig(
            device_name=name,
            power=light_source.power,
            power_unit=measurements.PowerUnit.MW
            if light_source.calibration is not None
            else measurements.PowerUnit.PERCENT,
        )

    def get_camera_task_from_channel_name(self, channel_name: str) -> _ChannelInformation:
        if "green" in channel_name:
            return _ChannelInformation(
                camera_name="camera_green_iso",
                light_source_name="light_source_blue",
                camera=self.rig_model.camera_green_iso,
                task=self.rig_model.light_source_blue.task,
                excitation_wavelength_nm=470,
                emission_wavelength_nm=525,
            )
        elif "iso" in channel_name:
            return _ChannelInformation(
                camera_name="camera_green_iso",
                light_source_name="light_source_uv",
                camera=self.rig_model.camera_green_iso,
                task=self.rig_model.light_source_uv.task,
                excitation_wavelength_nm=415,
                emission_wavelength_nm=520,
            )
        elif "red" in channel_name:
            return _ChannelInformation(
                camera_name="camera_red",
                light_source_name="light_source_lime",
                camera=self.rig_model.camera_red,
                task=self.rig_model.light_source_lime.task,
                excitation_wavelength_nm=565,
                emission_wavelength_nm=590,
            )
        else:
            raise ValueError(f"Invalid channel name: {channel_name}")

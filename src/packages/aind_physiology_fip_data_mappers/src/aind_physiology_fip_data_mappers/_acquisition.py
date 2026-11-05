"""Maps a FIP session to an aind-data-schema Acquisition.

Each FIP epoch in the session becomes one data stream, recording which patch cords were
acquired and how each of their channels was excited and imaged.

Classes:
    AindAcquisitionDataMapper: Maps a session directory to an ``Acquisition``.
"""

import json
import logging
import os
import sys
from datetime import datetime
from pathlib import Path
from typing import cast

import git
import pydantic
from aind_behavior_services.session import Session
from aind_data_schema.components import configs
from aind_data_schema.components.connections import Connection
from aind_data_schema.components.identifiers import Code
from aind_data_schema.core import acquisition
from aind_data_schema_models.modalities import Modality
from aind_data_schema_models.units import PowerUnit, SizeUnit, TimeUnit
from aind_physiology_fip import __semver__
from aind_physiology_fip.data_contract import dataset
from aind_physiology_fip.extra_types import RoiSettings
from aind_physiology_fip.rig import AindPhysioFipRig
from pandas import DataFrame

from aind_physiology_fip_data_mappers._base import DataMapper
from aind_physiology_fip_data_mappers._utils import (
    FIP_CHANNELS,
    GREEN_ISO_CAMERA,
    LED_SPECS,
    RED_CAMERA,
    LedId,
    TrackedDeviceName,
    patch_cord_name,
    snapshot_bonsai_environment,
)

logger = logging.getLogger(__name__)

# Any path inside the checkout this package runs from; the repository root is found by searching upward.
REPOSITORY_PATH = Path(__file__).resolve().parent


class _FipDataStreamMetadata(pydantic.BaseModel):
    """Timing and regions of interest for one FIP epoch, which becomes one data stream."""

    id: str
    start_time: pydantic.AwareDatetime
    end_time: pydantic.AwareDatetime
    regions: RoiSettings


def _fiber_name(fiber_number: int) -> str:
    """Name of an implanted fiber, as recorded in the subject's procedures."""
    return f"Fiber_{fiber_number}"


class AindAcquisitionDataMapper(DataMapper[acquisition.Acquisition]):
    """Maps a FIP session directory to an aind-data-schema ``Acquisition``."""

    def __init__(
        self,
        data_path: os.PathLike,
        repository_path: os.PathLike = REPOSITORY_PATH,
        session_end_time: datetime | None = None,
    ):
        """Create a mapper for one session directory.

        Args:
            data_path (os.PathLike): Session directory holding the ``fib/fip_*`` acquisition
                epochs.
            repository_path (os.PathLike): A path inside the checkout of the Aind.Physiology.Fip
                repository the session was acquired with. Its remote, commit and Bonsai
                environment are recorded as the code that produced the data. Defaults to the
                checkout this package runs from.
            session_end_time (datetime | None): When the session ended, as known to whoever
                launched it. Recorded as the acquisition end time when given, as the behavior
                mappers do; each data stream still ends when its epoch's data does. Left unset,
                the acquisition ends with its last data stream.
        """
        self._data_path = Path(data_path)
        self._repository_path = Path(repository_path)
        self._session_end_time = session_end_time
        self._mapped: acquisition.Acquisition | None = None

    def map(self) -> acquisition.Acquisition:
        """Map the session into an AIND acquisition."""
        logger.info("Mapping FIP acquisition to AIND schema.")
        self._mapped = self._map()
        return self._mapped

    def _map(self) -> acquisition.Acquisition:
        epochs = sorted(path for path in (self._data_path / "fib").glob("fip_*") if path.is_dir())
        session, rig = self._extract_session_and_rig(epochs)
        streams = self._extract_start_end_times(epochs)
        if not streams:
            raise ValueError(f"No FIP epoch under {self._data_path} has both regions and timing to map.")

        code = self._get_code()
        return acquisition.Acquisition(
            subject_id=session.subject,
            acquisition_start_time=min(stream.start_time for stream in streams),
            acquisition_end_time=self._session_end_time or max(stream.end_time for stream in streams),
            experimenters=session.experimenter,
            notes=session.notes,
            instrument_id=rig.rig_name,
            # Left blank on purpose: the behavior task FIP runs alongside supplies it.
            acquisition_type="",
            data_streams=[self._get_data_stream(rig, stream, code) for stream in streams],
        )

    def _get_code(self) -> list[Code]:
        """The workflow and the package that produced the session, at the commit it was acquired with."""
        repository = git.Repo(self._repository_path, search_parent_directories=True)
        if repository.working_tree_dir is None:
            raise ValueError(f"{self._repository_path} has no working tree to read the Bonsai environment from.")
        url = repository.remote().url
        commit_hash = repository.head.commit.hexsha
        bonsai = snapshot_bonsai_environment(Path(repository.working_tree_dir) / ".bonsai" / "Bonsai.config")

        python = sys.version_info
        python_version = f"{python.major}.{python.minor}.{python.micro}"
        if python.releaselevel != "final":
            python_version += f"-{python.releaselevel}.{python.serial}"

        return [
            Code(
                url=url,
                name="Aind.Physiology.Fip",
                version=__semver__,
                commit_hash=commit_hash,
                language="Bonsai",
                language_version=bonsai.get("Bonsai"),
                run_script=Path("src/main.bonsai"),
            ),
            Code(
                url=url,
                name="aind-physiology-fip",
                version=__semver__,
                commit_hash=commit_hash,
                language="Python",
                language_version=python_version,
            ),
        ]

    @classmethod
    def _get_data_stream(
        cls, rig: AindPhysioFipRig, stream: _FipDataStreamMetadata, code: list[Code]
    ) -> acquisition.DataStream:
        """One epoch: the patch cords acquired, and how each of their channels was excited and imaged."""
        light_sources = cls._get_light_source_configs(rig)
        detectors = cls._get_detector_configs(rig)

        # Devices every epoch uses: the LEDs, the cameras and the board that triggers them.
        active_devices = [config.device_name for config in light_sources.values()]
        active_devices.extend(config.device_name for config in detectors.values())
        active_devices.append(TrackedDeviceName.CUTTLEFISH)

        configurations: list = list(light_sources.values())
        connections = []

        # Each recorded fiber adds its patch cord and the implant the patch cord connects to.
        for roi, fiber in cls._get_recorded_fibers(stream):
            patch_cord, implant = patch_cord_name(roi), _fiber_name(fiber)
            active_devices.extend([patch_cord, implant])
            configurations.append(cls._get_patch_cord_config(roi, fiber, light_sources, detectors))
            connections.append(Connection(source_device=patch_cord, target_device=implant))

        return acquisition.DataStream(
            stream_start_time=stream.start_time,
            stream_end_time=stream.end_time,
            modalities=[Modality.FIB],
            code=code,
            active_devices=active_devices,
            configurations=configurations,
            connections=connections,
        )

    @staticmethod
    def _get_light_source_configs(rig: AindPhysioFipRig) -> dict[LedId, configs.LightEmittingDiodeConfig]:
        """LED settings, in the unit the rig's power is actually expressed in.

        A calibrated light source's power is in microwatts. Without a calibration it is the duty
        cycle as a fraction no greater than 1, which is recorded as a percentage.
        """
        light_sources = {
            LedId.UV: rig.light_source_uv,
            LedId.BLUE: rig.light_source_blue,
            LedId.LIME: rig.light_source_lime,
        }
        result = {}
        for led, light_source in light_sources.items():
            if light_source.calibration is None:
                power, unit = light_source.power * 100, PowerUnit.PERCENT
            else:
                power, unit = light_source.power, PowerUnit.UW
            result[led] = configs.LightEmittingDiodeConfig(
                device_name=LED_SPECS[led].name, power=power, power_unit=unit
            )
        return result

    @staticmethod
    def _get_detector_configs(rig: AindPhysioFipRig) -> dict[str, configs.DetectorConfig]:
        """Camera settings, keyed by camera name.

        Both cameras integrate for the light sources' ``delta_1``, which must therefore agree.
        """
        exposures = {
            light_source.task.delta_1
            for light_source in (rig.light_source_uv, rig.light_source_blue, rig.light_source_lime)
        }
        if len(exposures) != 1:
            raise ValueError(f"Light sources disagree on the camera exposure (delta_1, us): {sorted(exposures)}")
        exposure_ms = exposures.pop() / 1000

        return {
            camera.name: configs.DetectorConfig(
                device_name=camera.name,
                exposure_time=exposure_ms,
                exposure_time_unit=TimeUnit.MS,
                trigger_type=configs.TriggerType.INTERNAL,
            )
            for camera in (GREEN_ISO_CAMERA, RED_CAMERA)
        }

    @staticmethod
    def _get_recorded_fibers(stream: _FipDataStreamMetadata) -> list[tuple[int, int]]:
        """(ROI index, fiber number) for each ROI recorded as enabled and mapped to a fiber.

        ROI N on each camera sits over patch cord N. The ROI manager edits one table for both
        cameras, so an ROI's fiber and enabled state must agree between them.

        aind-data-schema requires at least one patch cord in a fiber photometry stream, so an
        epoch with no enabled, mapped ROI cannot be described and is rejected. That includes
        sessions predating fiber numbers, which already have their metadata.
        """
        green, red = stream.regions.camera_green_iso_roi, stream.regions.camera_red_roi
        if len(green) != len(red):
            raise ValueError(f"{stream.id} has {len(green)} green/iso ROIs but {len(red)} red ROIs.")

        fibers: list[tuple[int, int]] = []
        for index, (green_roi, red_roi) in enumerate(zip(green, red, strict=True)):
            if (green_roi.fiber_number, green_roi.enabled) != (red_roi.fiber_number, red_roi.enabled):
                raise ValueError(f"ROI {index} in {stream.id} has a different fiber or enabled state on each camera.")
            if green_roi.enabled and green_roi.fiber_number is not None:
                fibers.append((index, green_roi.fiber_number))

        fiber_numbers = [fiber for _, fiber in fibers]
        if len(set(fiber_numbers)) != len(fiber_numbers):
            raise ValueError(f"{stream.id} maps more than one ROI to the same fiber: {sorted(fiber_numbers)}")
        if not fibers:
            raise ValueError(
                f"No enabled ROI in {stream.id} is mapped to a fiber, and aind-data-schema requires at least one "
                "patch cord in a fiber photometry stream."
            )
        return fibers

    @staticmethod
    def _get_patch_cord_config(
        roi: int,
        fiber: int,
        light_sources: dict[LedId, configs.LightEmittingDiodeConfig],
        detectors: dict[str, configs.DetectorConfig],
    ) -> configs.PatchCordConfig:
        """The patch cord over ROI ``roi``, carrying one channel per LED and camera pairing."""
        return configs.PatchCordConfig(
            device_name=patch_cord_name(roi),
            channels=[
                configs.Channel(
                    channel_name=f"Fiber_{fiber}_{channel.name}",
                    detector=detectors[channel.camera.name],
                    light_sources=[light_sources[channel.led]],
                    variable_power=False,
                    excitation_filters=[],
                    emission_filters=[],
                    emission_wavelength=channel.emission_wavelength,
                    emission_wavelength_unit=SizeUnit.NM,
                )
                for channel in FIP_CHANNELS
            ],
        )

    @staticmethod
    def _extract_start_end_times(
        epochs: list[Path],
    ) -> list[_FipDataStreamMetadata]:
        """
        Extract timing metadata from all FIP acquisition epochs.

        For each epoch, reads the ROI settings from ``regions.json`` (epochs
        without it are skipped) and attempts to read start/end times from the
        ``SoftwareEvents/StartSessionTime.json`` and
        ``SoftwareEvents/EndSessionTime.json`` JSON-lines files (primary).
        Falls back to inferring times from camera CSV metadata streams if
        the software-event files are unavailable (legacy backup).

        Args:
            epochs (list[Path]): List of Path objects pointing to FIP
                epoch directories to process.

        Returns:
            list[_FipDataStreamMetadata]: List of metadata objects
                containing timing information for each processed epoch.

        Note:
            Failed epochs are logged as warnings and skipped, allowing
            partial processing of multi-epoch acquisitions.
        """
        data_streams = []
        for epoch in epochs:
            # Skip non-directory entries
            if not epoch.is_dir():
                continue

            # Extract the ROI settings from regions.json
            try:
                regions = cast(RoiSettings, dataset(root=epoch)["regions"].read())
            except Exception as e:  # noqa: BLE001
                logger.warning(f"Failed to read regions.json at {epoch}, skipping epoch: {e}")
                continue

            # Primary: read from SoftwareEvents JSON-lines files
            try:
                start_utc, end_utc = AindAcquisitionDataMapper._extract_times_from_software_events(epoch)
                data_streams.append(
                    _FipDataStreamMetadata(
                        id=epoch.name,
                        start_time=start_utc,
                        end_time=end_utc,
                        regions=regions,
                    )
                )
                continue
            except Exception as e:  # noqa: BLE001
                logger.warning(f"SoftwareEvents timing unavailable at {epoch}, falling back to CSV: {e}")

            # Fallback: infer times from camera metadata CSV streams
            _candidate_streams = [
                "camera_green_iso_metadata",
                "camera_red_metadata",
            ]
            try:
                this_epoch = dataset(root=epoch)
                for stream in _candidate_streams:
                    try:
                        logger.debug(f"Checking for timing in stream: {stream}")
                        start_utc, end_utc = AindAcquisitionDataMapper._extract_from_df(
                            cast(DataFrame, this_epoch[stream].read())
                        )
                        data_streams.append(
                            _FipDataStreamMetadata(
                                id=epoch.name,
                                start_time=start_utc,
                                end_time=end_utc,
                                regions=regions,
                            )
                        )
                        break  # One stream is enough to establish epoch timing
                    except Exception as e:  # noqa: BLE001
                        logger.debug(f"Stream '{stream}' unavailable at {epoch}: {e}")
            except Exception as e:  # noqa: BLE001
                logger.warning(f"Failed to load FIP dataset at {epoch}: {e}")

        return data_streams

    @staticmethod
    def _extract_times_from_software_events(epoch: Path) -> tuple[datetime, datetime]:
        """
        Extract start and end times from the SoftwareEvents JSON-lines files.

        Reads the last entry of ``SoftwareEvents/StartSessionTime.json`` and
        ``SoftwareEvents/EndSessionTime.json`` within the epoch directory and
        parses the ``data`` field as an ISO 8601 datetime.

        Args:
            epoch (Path): Path to the FIP epoch directory.

        Returns:
            tuple[datetime, datetime]: A tuple of (start_time, end_time).

        Raises:
            FileNotFoundError: If either JSON-lines file does not exist.
            ValueError: If a file is empty or the ``data`` field is missing.
        """
        start_path = epoch / "SoftwareEvents" / "StartSessionTime.json"
        end_path = epoch / "SoftwareEvents" / "EndSessionTime.json"
        start_time = AindAcquisitionDataMapper._read_last_software_event_datetime(start_path)
        end_time = AindAcquisitionDataMapper._read_last_software_event_datetime(end_path)
        return start_time, end_time

    @staticmethod
    def _read_last_software_event_datetime(path: Path) -> datetime:
        """
        Parse the datetime from the last entry of a software-events JSON-lines file.

        Each line in the file is a JSON object; the ``data`` field of the last
        non-empty line is interpreted as an ISO 8601 datetime string.

        Args:
            path (Path): Path to the JSON-lines file.

        Returns:
            datetime: Parsed datetime from the ``data`` field of the last entry.

        Raises:
            FileNotFoundError: If the file does not exist.
            ValueError: If the file contains no valid entries or the ``data``
                field is absent.
        """
        if not path.exists():
            raise FileNotFoundError(f"Software events file not found: {path}")
        last_line: str | None = None
        with open(path, encoding="utf-8") as f:
            for line in f:
                stripped = line.strip()
                if stripped:
                    last_line = stripped
        if last_line is None:
            raise ValueError(f"No entries found in {path}")
        event = json.loads(last_line)
        if "data" not in event:
            raise ValueError(f"Missing 'data' field in last entry of {path}")
        return datetime.fromisoformat(event["data"])

    @staticmethod
    def _extract_from_df(df: DataFrame) -> tuple[datetime, datetime]:
        """
        Extract start and end timestamps from a camera-metadata DataFrame.

        Retrieves the first and last ``CpuTime`` entries to determine the
        temporal boundaries of data collection.

        Args:
            df (DataFrame): Pandas DataFrame containing a ``CpuTime`` column
                with ISO 8601 datetime strings.

        Returns:
            tuple[datetime, datetime]: A tuple containing the start time
                (first entry) and end time (last entry).

        Raises:
            ValueError: If the DataFrame is ``None`` or empty.
        """
        if df is None or df.empty:
            raise ValueError("DataFrame is None or empty.")
        # Parse ISO format timestamps from first and last rows
        start_utc = datetime.fromisoformat(df["CpuTime"].iloc[0])
        end_utc = datetime.fromisoformat(df["CpuTime"].iloc[-1])
        return start_utc, end_utc

    @staticmethod
    def _extract_session_and_rig(
        epochs: list[Path],
    ) -> tuple[Session, AindPhysioFipRig]:
        """
        Extract session and rig configuration from acquisition epochs.

        Searches through the provided epochs to find valid session and rig
        configuration data. Returns the first valid session and rig found
        across all epochs.

        Args:
            epochs (list[Path]): List of Path objects pointing to FIP
                epoch directories to search.

        Returns:
            tuple[Session, AindPhysioFipRig]: A tuple
                containing the session model and rig configuration.

        Raises:
            ValueError: If no valid session_input or rig_input is found
                in any of the provided epochs.
        """
        session: Session | None = None
        rig: AindPhysioFipRig | None = None
        for epoch in epochs:
            # Skip non-directory entries
            if not epoch.is_dir():
                continue
            # Load the dataset for this epoch
            _dataset = dataset(root=epoch)

            # Try to extract session metadata if not already found
            if session is None:
                try:
                    session = _dataset["session_input"].read()
                except Exception as e:  # noqa: BLE001
                    logger.debug(f"No session_input found in dataset at {epoch}: {e}")

            # Try to extract rig configuration if not already found
            if rig is None:
                try:
                    rig = _dataset["rig_input"].read()
                except Exception as e:  # noqa: BLE001
                    logger.debug(f"No rig_input found in dataset at {epoch}: {e}")
                    continue

        # Validate that required metadata was found
        if session is None:
            raise ValueError("No session_input found in any of the provided epochs.")
        if rig is None:
            raise ValueError("No rig_input found in any of the provided epochs.")
        return session, rig

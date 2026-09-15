"""Maps to aind-data-schema instrument file"""

import logging
import os
import platform
from datetime import datetime
from pathlib import Path

from aind_data_schema.components import coordinates, devices
from aind_data_schema.components.connections import Connection
from aind_data_schema.core import instrument
from aind_data_schema_models.modalities import Modality

from aind_physiology_fip.data_contract import dataset
from aind_physiology_fip.data_mappers._utils import (
    FilterId,
    LedId,
    TrackedDeviceName,
    TrackedDevicesInfo,
    camera_frame_metadata,
    make_filter,
    make_led,
    snap_to_grid,
)
from aind_physiology_fip.rig import AindPhysioFipRig, FipCamera

logger = logging.getLogger(__name__)


class AindInstrumentDataMapper:
    """Mapper for AIND FIP photometry rigs into aind-data-schema Instrument objects."""

    def __init__(self, data_path: os.PathLike):
        self._data_path = Path(data_path)
        self._mapped: instrument.Instrument | None = None

    @property
    def mapped(self) -> instrument.Instrument:
        if self._mapped is None:
            raise ValueError("Data has not been mapped yet.")
        return self._mapped

    def map(self) -> instrument.Instrument:
        """Map the rig JSON input into an AIND instrument."""
        logger.info("Mapping FIP photometry rig to AIND schema.")
        self._mapped = self._map(self._data_path)
        return self.mapped

    @classmethod
    def _map(cls, root_path: os.PathLike) -> instrument.Instrument:
        """Helper to map to instrument schema"""
        epoch, rig = cls._find_epoch(Path(root_path))

        computer = cls._get_computer(rig)
        patch_coords = cls._get_fiber_patch_cords()
        light_sources = cls._get_light_sources()
        detectors = cls._get_detectors(rig, epoch)
        filters = cls._get_filters()
        lens = cls._get_lens()
        cuttlefish_device = cls._get_cuttlefish_device(rig)
        white_rabbit = cls._get_white_rabbit_device()
        objective = cls._get_objective()

        connections = [
            Connection(
                source_device=cuttlefish_device.name,
                source_port=rig.cuttlefish_fip.port_name,
                target_device=computer.name,
            ),
            Connection(
                source_device=white_rabbit.name,
                source_port=TrackedDevicesInfo.PORT_CLOCK,
                target_device=cuttlefish_device.name,
                target_port=rig.cuttlefish_fip.port_name,
                send_and_receive=False,
            ),
        ]

        # Put everything in a list and unwrap lists
        all_components = []
        for item in [
            computer,
            patch_coords,
            light_sources,
            detectors,
            objective,
            filters,
            lens,
            cuttlefish_device,
            white_rabbit,
        ]:
            if isinstance(item, list):
                all_components.extend(item)  # unwrap lists
            else:
                all_components.append(item)  # keep single items

        # Coordinate system matching behavior (bregma with X/Y/Z axes, not BREGMA_ARI)
        coordinate_system = coordinates.CoordinateSystem(
            name="origin",
            origin=coordinates.Origin.BREGMA,
            axis_unit=coordinates.SizeUnit.MM,
            axes=[
                coordinates.Axis(name=coordinates.AxisName.X, direction=coordinates.Direction.LR),
                coordinates.Axis(name=coordinates.AxisName.Y, direction=coordinates.Direction.AP),
                coordinates.Axis(name=coordinates.AxisName.Z, direction=coordinates.Direction.IS),
            ],
        )

        return instrument.Instrument(
            instrument_id=rig.rig_name,
            modalities=[Modality.FIB],
            modification_date=datetime.now().astimezone().date(),
            components=all_components,
            coordinate_system=coordinate_system,
            connections=connections,
        )

    @staticmethod
    def _find_epoch(root_path: Path) -> tuple[Path, AindPhysioFipRig]:
        """Find the FIP epoch holding the rig configuration, and read it.

        Mirrors ``ProtoAcquisitionMapper``: epochs live at ``<root>/fib/fip_*`` and their
        contents are addressed through the data contract rather than by hand-built paths.
        """
        epochs = sorted(path for path in (root_path / "fib").glob("fip_*") if path.is_dir())
        if not epochs:
            raise ValueError(f"No FIP epochs (fib/fip_*) found in {root_path}.")
        for epoch in epochs:
            try:
                return epoch, dataset(root=epoch)["rig_input"].read()
            except (OSError, ValueError, KeyError) as e:
                # Missing file, malformed JSON or a rig that fails validation: try the next epoch.
                logger.debug("No readable rig_input in %s: %s", epoch, e)
        raise ValueError(f"No readable rig_input.json in any FIP epoch under {root_path}.")

    @staticmethod
    def _get_objective() -> devices.Objective:
        return devices.Objective(
            model=TrackedDevicesInfo.OBJECTIVE_MODEL,
            name=TrackedDeviceName.OBJECTIVE,
            serial_number=TrackedDevicesInfo.OBJECTIVE_SERIAL_NUMBER,
            manufacturer=devices.Organization.NIKON,
            numerical_aperture=TrackedDevicesInfo.OBJECTIVE_NUMERICAL_APERTURE,
            magnification=TrackedDevicesInfo.OBJECTIVE_MAGNIFICATION,
            immersion=devices.ImmersionMedium.AIR,
        )

    @staticmethod
    def _get_computer(rig: AindPhysioFipRig) -> devices.Computer:
        """Gets the computer metadata"""
        return devices.Computer(
            name=rig.computer_name,
            manufacturer=devices.Organization.AIND,
            operating_system=platform.platform(),
        )

    @staticmethod
    def _get_fiber_patch_cords() -> list[devices.FiberPatchCord]:
        """Return the four patch cords used in the FIP rig."""
        note = (
            "All four patch cords are a single device at the camera end "
            "with four connections to up to four implanted fibers."
        )
        return [
            devices.FiberPatchCord(
                name=f"Patch Cord {i}",
                manufacturer=devices.Organization.DORIC,
                model=TrackedDevicesInfo.PATCH_CORD_MODEL,
                core_diameter=TrackedDevicesInfo.PATCH_CORD_DIAMETER,
                numerical_aperture=TrackedDevicesInfo.PATCH_CORD_NUMERICAL_APERTURE,
                notes=note,
            )
            for i in range(4)
        ]

    @staticmethod
    def _get_light_sources() -> list[devices.LightEmittingDiode]:
        """Return all LEDs used in the rig."""
        return [make_led(color) for color in LedId]

    @staticmethod
    def _get_detectors(rig: AindPhysioFipRig, epoch: Path) -> list[devices.Detector]:
        """Return list of cameras / detectors in the rig."""

        def _get_detector(name: TrackedDeviceName, cam: FipCamera) -> devices.Detector:
            metadata = camera_frame_metadata(epoch, name)
            """Returns the detector"""
            return devices.Detector(
                name=name,
                serial_number=cam.serial_number,
                manufacturer=devices.Organization.FLIR,
                model=TrackedDevicesInfo.DETECTOR_MODEL,
                detector_type=devices.DetectorType.CAMERA,
                data_interface=devices.DataInterface.USB,
                cooling=devices.Cooling.AIR,
                immersion=devices.ImmersionMedium.AIR,
                bin_width=TrackedDevicesInfo.DETECTOR_BIN_WIDTH,
                bin_height=TrackedDevicesInfo.DETECTOR_BIN_HEIGHT,
                bin_mode=devices.BinMode.ADDITIVE,
                crop_offset_x=snap_to_grid(cam.offset.x, TrackedDevicesInfo.DETECTOR_BIN_WIDTH),
                crop_offset_y=snap_to_grid(cam.offset.y, TrackedDevicesInfo.DETECTOR_BIN_HEIGHT),
                crop_width=metadata.crop_width,
                crop_height=metadata.crop_height,
                gain=cam.gain,
                chroma=devices.CameraChroma.BW,
                bit_depth=metadata.bit_depth,
            )

        return [
            _get_detector(TrackedDeviceName.CAMERA_GREEN_ISO, rig.camera_green_iso),
            _get_detector(TrackedDeviceName.CAMERA_RED, rig.camera_red),
        ]

    @staticmethod
    def _get_white_rabbit_device() -> devices.HarpDevice:
        """Gets the white rabbit device"""
        return devices.HarpDevice(
            name=TrackedDeviceName.CLOCK_GENERATOR,
            harp_device_type=devices.HarpDeviceType.WHITERABBIT,
            manufacturer=devices.Organization.AIND,
            is_clock_generator=True,
            channels=[
                devices.DAQChannel(channel_name="ClkOut", channel_type=devices.DaqChannelType.DO),
            ],
        )

    @staticmethod
    def _get_cuttlefish_device(rig: AindPhysioFipRig) -> devices.HarpDevice:
        """Gets the cuttlefish device"""
        return devices.HarpDevice(
            name=TrackedDeviceName.CUTTLEFISH,
            serial_number=rig.cuttlefish_fip.serial_number,
            harp_device_type=devices.HarpDeviceType.CUTTLEFISHFIP,
            manufacturer=devices.Organization.OEPS,
            is_clock_generator=False,
            data_interface=devices.DataInterface.USB,
        )

    @staticmethod
    def _get_lens() -> devices.Lens:
        """Gets the lens used"""
        return devices.Lens(
            manufacturer=devices.Organization.THORLABS,
            model=TrackedDevicesInfo.LENS_MODEL,
            name=TrackedDeviceName.LENS,
        )

    @staticmethod
    def _get_filters() -> list[devices.Filter]:
        """Return optical filters used in the rig."""
        return [
            make_filter(fid)
            for fid in [
                FilterId.GREEN_EMISSION,
                FilterId.RED_EMISSION,
                FilterId.EMISSION_DICHROIC,
                FilterId.DUAL_EDGE_DICHROIC,
                FilterId.EXCITATION_410,
                FilterId.EXCITATION_470,
                FilterId.EXCITATION_560,
                FilterId.DICHROIC_450_LONGPASS,
                FilterId.DICHROIC_500_LONGPASS,
            ]
        ]

"""Maps to aind-data-schema instrument file"""

import logging
import os
import platform
from datetime import datetime
from pathlib import Path

from aind_data_schema.components import coordinates, devices, measurements
from aind_data_schema.components.connections import Connection
from aind_data_schema.core import instrument
from aind_data_schema_models.modalities import Modality
from aind_data_schema_models.units import PowerUnit

from aind_physiology_fip.data_contract import dataset
from aind_physiology_fip.data_mappers._utils import (
    LED_SPECS,
    FilterId,
    LedId,
    TrackedDeviceName,
    TrackedDevicesInfo,
    camera_frame_metadata,
    make_filter,
    make_led,
    port_names,
    snap_to_grid,
)
from aind_physiology_fip.rig import AindPhysioFipRig, FipCamera, LightSource

logger = logging.getLogger(__name__)


class AindInstrumentDataMapper:
    """Mapper for AIND FIP photometry rigs into aind-data-schema Instrument objects."""

    def __init__(
        self,
        data_path: os.PathLike,
        clock_generator_name: str | None = None,
        objective_serial_number: str | None = None,
    ):
        """Create a mapper for one session directory.

        Args:
            data_path (os.PathLike): Session directory holding the ``fib/fip_*`` acquisition
                epochs.
            clock_generator_name (str | None): Name of the Harp clock generator (WhiteRabbit)
                that synchronises the cuttlefish board. When FIP is acquired alongside a behavior
                task, the clock generator belongs to that task and may be named differently by
                each, so the name is supplied rather than assumed in order for the two
                instruments to agree. Left unset, as for a FIP-only acquisition, no clock
                generator is recorded at all, since nothing on the FIP side says one is present.
            objective_serial_number (str | None): Serial number of the objective, which differs
                between instruments. It is not part of the rig configuration because the workflow
                does not use it and nothing can validate it against the hardware, so it is
                supplied per instrument instead. Through the CLI it may be set once per
                acquisition computer as the ``OBJECTIVE_SERIAL_NUMBER`` environment variable.
                Left unset, the objective is recorded without a serial.
        """
        self._data_path = Path(data_path)
        self._clock_generator_name = clock_generator_name
        self._objective_serial_number = objective_serial_number
        self._mapped: instrument.Instrument | None = None

    @property
    def mapped(self) -> instrument.Instrument:
        if self._mapped is None:
            raise ValueError("Data has not been mapped yet.")
        return self._mapped

    def map(self) -> instrument.Instrument:
        """Map the rig JSON input into an AIND instrument."""
        logger.info("Mapping FIP photometry rig to AIND schema.")
        self._mapped = self._map(self._data_path, self._clock_generator_name, self._objective_serial_number)
        return self.mapped

    @classmethod
    def _map(
        cls,
        root_path: os.PathLike,
        clock_generator_name: str | None = None,
        objective_serial_number: str | None = None,
    ) -> instrument.Instrument:
        """Helper to map to instrument schema"""
        epoch, rig = cls._find_epoch(Path(root_path))

        computer = cls._get_computer(rig)
        patch_coords = cls._get_fiber_patch_cords()
        light_sources = cls._get_light_sources()
        detectors = cls._get_detectors(rig, epoch)
        filters = cls._get_filters()
        lens = cls._get_lens()
        cuttlefish_device = cls._get_cuttlefish_device(rig)
        objective = cls._get_objective(objective_serial_number)

        connections = cls._get_trigger_connections(rig, cuttlefish_device.name)

        # The device and its connection are emitted together: aind-data-schema rejects a
        # connection that names a device absent from the instrument.
        clock_generator: list[devices.HarpDevice] = []
        if clock_generator_name is not None:
            white_rabbit = cls._get_white_rabbit_device(clock_generator_name)
            clock_generator.append(white_rabbit)
            connections.insert(
                0,
                Connection(
                    source_device=white_rabbit.name,
                    target_device=cuttlefish_device.name,
                    source_port="ClkOut",
                    target_port="ClkIn",
                ),
            )

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
            clock_generator,
        ]:
            if isinstance(item, list):
                all_components.extend(item)  # unwrap lists
            else:
                all_components.append(item)  # keep single items

        connections.extend(
            cls._get_connections_from_computer_to_type(
                computer,
                [devices.HarpDevice, devices.Detector],
                all_components,
                source_port="USB",
                target_port="USB",
                send_and_receive=True,
            )
        )

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

        mapped_at = datetime.now().astimezone()

        return instrument.Instrument(
            instrument_id=rig.rig_name,
            modalities=[Modality.FIB],
            modification_date=mapped_at.date(),
            calibrations=cls._get_calibrations(rig, mapped_at),
            components=all_components,
            coordinate_system=coordinate_system,
            connections=connections,
        )

    @staticmethod
    def _read_rig(epoch: Path) -> AindPhysioFipRig | None:
        """The rig input an epoch logged, or None if it has none that can be read."""
        try:
            return dataset(root=epoch)["rig_input"].read()
        except (FileNotFoundError, ValueError, KeyError) as e:
            # No rig_input, malformed JSON or a rig that fails validation.
            logger.debug("No readable rig_input in %s: %s", epoch, e)
            return None

    @classmethod
    def _find_epoch(cls, root_path: Path) -> tuple[Path, AindPhysioFipRig]:
        """Read the rig configuration from the session's FIP epochs, checking they all agree.

        Epochs live at ``<root>/fib/fip_*`` and are addressed through the data contract rather
        than by hand-built paths. A session can hold several epochs, each logging its own rig
        input, but they all describe one instrument, so every rig input found must be
        identical. Once that holds, the first epoch is used.
        """
        epochs = sorted(path for path in (root_path / "fib").glob("fip_*") if path.is_dir())
        if not epochs:
            raise ValueError(f"No FIP epochs (fib/fip_*) found in {root_path}.")

        # Read every epoch's rig input, skipping epochs that have none.
        readable: list[tuple[Path, AindPhysioFipRig]] = []
        for epoch in epochs:
            rig = cls._read_rig(epoch)
            if rig is not None:
                readable.append((epoch, rig))
        if not readable:
            raise ValueError(f"No readable rig_input.json in any FIP epoch under {root_path}.")

        # Matching the first is enough for all epochs to match each other.
        first_epoch, first_rig = readable[0]
        for epoch, rig in readable[1:]:
            if rig != first_rig:
                raise ValueError(
                    f"Rig input in {epoch.name} differs from {first_epoch.name}. "
                    "Epochs of one session must describe the same instrument."
                )
        return first_epoch, first_rig

    @staticmethod
    def _light_sources(rig: AindPhysioFipRig) -> list[tuple[LedId, LightSource, TrackedDeviceName]]:
        """Each light source with its LED identity and the camera triggered alongside it."""
        return [
            (LedId.UV, rig.light_source_uv, TrackedDeviceName.CAMERA_GREEN_ISO),
            (LedId.BLUE, rig.light_source_blue, TrackedDeviceName.CAMERA_GREEN_ISO),
            (LedId.LIME, rig.light_source_lime, TrackedDeviceName.CAMERA_RED),
        ]

    @classmethod
    def _get_calibrations(cls, rig: AindPhysioFipRig, calibrated_at: datetime) -> list[measurements.PowerCalibration]:
        """Power-against-duty-cycle calibrations for the light sources that carry one.

        The rig stores duty cycle as a fraction (``le=1``) while the only non-absolute input
        unit aind-data-schema offers is ``percent``, whose inputs are percentage strengths, so
        the look-up table inputs are scaled to 0-100. Outputs are passed through in the unit
        ``LightSourcePower`` declares, which is microwatts.
        """
        calibrations = []
        for led, light_source, _ in cls._light_sources(rig):
            if light_source.calibration is None:
                continue
            lut = sorted(light_source.calibration.power_lut.items())
            calibrations.append(
                measurements.PowerCalibration(
                    device_name=LED_SPECS[led].name,
                    calibration_date=calibrated_at,
                    input=[duty_cycle * 100 for duty_cycle, _ in lut],
                    input_unit=PowerUnit.PERCENT,
                    output=[power for _, power in lut],
                    output_unit=PowerUnit.UW,
                )
            )
        return calibrations

    @classmethod
    def _get_trigger_connections(cls, rig: AindPhysioFipRig, source_device: str) -> list[Connection]:
        """Connections from the cuttlefish board out to the light sources and cameras it triggers.

        Each light source's task names the board line that fires the LED and the line that fires
        the camera recording it. The green/iso camera is driven by two light sources over one
        line, so camera connections are de-duplicated.
        """
        connections: list[Connection] = []
        camera_lines: dict[tuple[str, TrackedDeviceName], None] = {}
        for led, light_source, camera in cls._light_sources(rig):
            for port in port_names(light_source.task.light_source_port):
                connections.append(
                    Connection(source_device=source_device, source_port=port, target_device=LED_SPECS[led].name)
                )
            for port in port_names(light_source.task.camera_port):
                camera_lines[(port, camera)] = None

        connections.extend(
            Connection(source_device=source_device, source_port=port, target_device=camera)
            for port, camera in camera_lines
        )
        return connections

    @staticmethod
    def _get_objective(serial_number: str | None) -> devices.Objective:
        """Gets the objective, whose serial number is supplied per instrument."""
        if serial_number is None:
            logger.warning("No objective serial number supplied; recording the objective without one.")
        else:
            logger.info("Using objective serial number %s.", serial_number)
        return devices.Objective(
            model=TrackedDevicesInfo.OBJECTIVE_MODEL,
            name=TrackedDeviceName.OBJECTIVE,
            serial_number=serial_number,
            manufacturer=devices.Organization.NIKON,
            numerical_aperture=TrackedDevicesInfo.OBJECTIVE_NUMERICAL_APERTURE,
            magnification=TrackedDevicesInfo.OBJECTIVE_MAGNIFICATION,
            immersion=devices.ImmersionMedium.AIR,
        )

    @staticmethod
    def _get_connections_from_computer_to_type(
        computer: devices.Computer,
        types: list[type[devices.Device]],
        components: list[devices.Device],
        source_port: str | None = None,
        target_port: str | None = None,
        send_and_receive: bool = True,
    ) -> list[Connection]:
        result_connections = []
        computer_name = computer.name

        for device in components:
            if any(isinstance(device, device_type) for device_type in types):
                result_connections.append(
                    Connection(
                        source_device=computer_name,
                        target_device=device.name,
                        source_port=source_port,
                        target_port=target_port,
                        send_and_receive=send_and_receive,
                    )
                )
        return result_connections

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
    def _get_white_rabbit_device(name: str) -> devices.HarpDevice:
        """Gets the white rabbit device"""
        return devices.HarpDevice(
            name=name,
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

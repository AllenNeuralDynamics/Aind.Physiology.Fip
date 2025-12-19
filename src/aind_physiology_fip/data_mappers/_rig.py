"""Maps to aind-data-schema instrument file"""
import logging
import os
import platform
from datetime import date
from pathlib import Path
from typing import Optional, List

from aind_behavior_services.utils import model_from_json_file

import aind_data_schema.components.devices as devices
from aind_data_schema.components.connections import Connection
import aind_data_schema.core.instrument as instrument
from aind_data_schema_models.modalities import Modality

from aind_physiology_fip.rig import AindPhysioFipRig, FipCamera
from aind_data_schema_models import units

from ._utils import TrackedDevicesInfo

logger = logging.getLogger(__name__)


class AindInstrumentDataMapper:
    """Mapper for AIND FIP photometry rigs into aind-data-schema Instrument objects."""

    def __init__(self, data_path: os.PathLike):
        self._data_path = Path(data_path)
        self._mapped: Optional[instrument.Instrument] = None

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
        """Helper to map to insturment schema"""
        rig = model_from_json_file(Path(root_path) / "rig_input.json", AindPhysioFipRig)

        computer = cls._get_computer(rig)
        patch_coords = cls._get_fiber_patch_cords()
        light_sources = cls._get_light_sources(rig)
        detectors = cls._get_detectors(rig)
        filters = cls._get_filters()
        lens = cls._get_lens()
        cuttlefish_device = cls._get_cuttlefish_device(rig)
        white_rabbit = cls._get_white_rabbit_device()

        connections = [
            Connection(
                source_device=cuttlefish_device.name,
                source_port=TrackedDevicesInfo.PORT_COM,
                target_device=computer.name
            ),
            Connection(
                source_device=white_rabbit.name,
                source_port=TrackedDevicesInfo.PORT_CLOCK,
                target_device=cuttlefish_device.name,
                target_port=TrackedDevicesInfo.PORT_COM,
                send_and_receive=False
            )
        ]

        # Put everything in a list and unwrap lists
        all_components = []
        for item in [computer, patch_coords, light_sources, detectors, filters, lens, cuttlefish_device, white_rabbit]:
            if isinstance(item, list):
                all_components.extend(item)  # unwrap lists
            else:
                all_components.append(item)  # keep single items

        # Coordinate system matching behavior (bregma with X/Y/Z axes, not BREGMA_ARI)
        coordinate_system = {
            "object_type": "Coordinate system",
            "name": "origin",
            "origin": "Bregma",
            "axes": [
                {"object_type": "Axis", "name": "X", "direction": "Left_to_right"},
                {"object_type": "Axis", "name": "Y", "direction": "Anterior_to_posterior"},
                {"object_type": "Axis", "name": "Z", "direction": "Inferior_to_superior"},
            ],
            "axis_unit": "millimeter",
        }

        return instrument.Instrument(
            instrument_id=rig.rig_name,
            modalities=[Modality.FIB],
            modification_date=date.today(),
            components=all_components,
            coordinate_system=coordinate_system,
            connections=connections
        )

    @staticmethod
    def _get_computer(rig: AindPhysioFipRig) -> devices.Computer:
        """Gets the computer metadata"""
        return devices.Computer(
            name=TrackedDevicesInfo.COMPUTER,
            manufacturer=devices.Organization.AIND,
            operating_system=platform.platform(),
            serial_number=rig.computer_name
        )

    @staticmethod
    def _get_fiber_patch_cords() -> List[devices.FiberPatchCord]:
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
    def _get_light_sources(rig: AindPhysioFipRig) -> List[devices.LightEmittingDiode]:
        """Return all LEDs used in the rig."""
        sources = []
        for color, src in [("uv", rig.light_source_uv), ("blue", rig.light_source_blue), ("lime", rig.light_source_lime)]:
            sources.append(
                devices.LightEmittingDiode(
                    name=src.name,
                    manufacturer=devices.Organization.THORLABS,
                    model={"uv": "M470F3", "blue": "M415F3", "lime": "M565F3"}[color],
                    wavelength=int(src.power),
                    wavelength_unit=units.SizeUnit.NM,
                )
            )
        return sources

    @staticmethod
    def _get_detectors(rig: AindPhysioFipRig) -> List[devices.Detector]:
        def _get_detector(cam: FipCamera) -> devices.Detector:
            """Returns the detector"""
            return devices.Detector(
                name=cam.name,
                serial_number=cam.serial_number,
                model=TrackedDevicesInfo.DETECTOR_MODEL,
                detector_type=devices.DetectorType.CAMERA,
                data_interface=devices.DataInterface.USB,
                cooling=devices.Cooling.AIR,
                immersion=devices.ImmersionMedium.AIR,
                bin_width=TrackedDevicesInfo.DETECTOR_BIN_WIDTH,
                bin_height=TrackedDevicesInfo.DETECTOR_BIN_HEIGHT,
                bin_mode=devices.BinMode.ADDITIVE,
                crop_offset_x=cam.offset.x,
                crop_offset_y=cam.offset.y,
                crop_width=TrackedDevicesInfo.DETECTOR_CROP_WIDTH,
                crop_height=TrackedDevicesInfo.DETECTOR_CROP_HEIGHT,
                gain=cam.gain,
                chroma=devices.CameraChroma.BW,
                bit_depth=TrackedDevicesInfo.DETECTOR_BIT_DEPTH,
            )
    
        """Return list of cameras / detectors in the rig."""
        detectors = []
        for cam_attr in ["camera_red", "camera_green_iso"]:
            cam: FipCamera = getattr(rig, cam_attr)
            detectors.append(
                _get_detector(cam)
            )
        return detectors

    @staticmethod
    def _get_white_rabbit_device() -> devices.HarpDevice:
        """Gets the white rabbit device"""
        return devices.HarpDevice(
            name=TrackedDevicesInfo.WHITE_RABBIT_DEVICE_NAME,
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
            name=rig.cuttlefish_fip.name,
            harp_device_type=devices.HarpDeviceType.CUTTLEFISHFIP,
            is_clock_generator=False,
            data_interface=devices.DataInterface.USB
        )
        
    @staticmethod
    def _get_lens() -> devices.Lens:
        """Gets the lens used"""
        return devices.Lens(
            manufacturer=devices.Organization.THORLABS,
            model=TrackedDevicesInfo.LENS_MODEL,
            name=TrackedDevicesInfo.LENS_NAME,
        )
    
    @staticmethod
    def _get_filters() -> List[devices.Filter]:
        """Return optical filters used in the rig."""
        return [
            devices.Filter(
                name="Green emission filter",
                manufacturer=devices.Organization.SEMROCK,
                model="FF01-520/35-25",
                filter_type="Band pass",
                center_wavelength=520,
            ),
            devices.Filter(
                name="Red emission filter",
                manufacturer=devices.Organization.SEMROCK,
                model="FF01-600/37-25",
                filter_type="Band pass",
                center_wavelength=600,
            ),
            devices.Filter(
                name="Emission Dichroic",
                model="FF562-Di03-25x36",
                manufacturer=devices.Organization.SEMROCK,
                filter_type="Dichroic",
                cut_off_wavelength=562,
            ),
            devices.Filter(
                name="dual-edge standard epi-fluorescence dichroic beamsplitter",
                model="FF493/574-Di01-25x36",
                manufacturer=devices.Organization.SEMROCK,
                notes="BrightLine dual-edge standard epi-fluorescence dichroic beamsplitter",
                filter_type="Multiband",
                center_wavelength=[493, 574],
            ),
            devices.Filter(
                name="Excitation filter 410nm",
                manufacturer=devices.Organization.THORLABS,
                model="FB410-10",
                filter_type="Band pass",
                center_wavelength=410,
            ),
            devices.Filter(
                name="Excitation filter 470nm",
                manufacturer=devices.Organization.THORLABS,
                model="FB470-10",
                filter_type="Band pass",
                center_wavelength=470,
            ),
            devices.Filter(
                name="Excitation filter 560nm",
                manufacturer=devices.Organization.THORLABS,
                model="FB560-10",
                filter_type="Band pass",
                center_wavelength=560,
            ),
            devices.Filter(
                name="450 Dichroic Longpass Filter",
                manufacturer=devices.Organization.EDMUND_OPTICS,
                model="#69-898",
                filter_type="Dichroic",
                cut_off_wavelength=450,
            ),
            devices.Filter(
                name="500 Dichroic Longpass Filter",
                manufacturer=devices.Organization.EDMUND_OPTICS,
                model="#69-899",
                filter_type="Dichroic",
                cut_off_wavelength=500,
            )
        ]

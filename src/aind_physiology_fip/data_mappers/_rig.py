import dataclasses
import logging
import os
import platform
import socket
from datetime import date
from decimal import Decimal
from pathlib import Path
from typing import Optional, List, Tuple

from aind_behavior_services.utils import model_from_json_file

import aind_data_schema.components.devices as devices
import aind_data_schema.components.connections as connections
import aind_data_schema.core.instrument as instrument
from aind_data_schema_models.modalities import Modality
from aind_data_schema.base import GenericModel

from aind_physiology_fip.rig import AindPhysioFipRig, FipCamera
from aind_data_schema_models import units

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
        rig = model_from_json_file(Path(root_path) / "rig_input.json", AindPhysioFipRig)

        # TODO finish this
        return instrument.Instrument(
            instrument_id=rig.rig_name,
            modalities=[Modality.FIB],
            modification_date=date.today(),
        )

    @staticmethod
    def _get_computer(rig: AindPhysioFipRig) -> devices.Computer:
        return devices.Computer(
            name=socket.gethostname(),
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
                model="BBP(4)_200/220/900-0.37_Custom_FCM-4xMF1.25",
                core_diameter=200,
                numerical_aperture=0.37,
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
        """Return cameras / detectors in the rig."""
        detectors = []
        for cam_attr in ["camera_red", "camera_green_iso"]:
            if not hasattr(rig, cam_attr):
                continue
            cam: FipCamera = getattr(rig, cam_attr)
            detectors.append(
                devices.Detector(
                    name=cam.name,
                    serial_number=cam.serial_number,
                    model="BFS-U3-20S40M",
                    detector_type="Camera",
                    data_interface="USB",
                    cooling="Air",
                    immersion="air",
                    bin_width=4,
                    bin_height=4,
                    bin_mode="Additive",
                    crop_offset_x=cam.offset.x if hasattr(cam, "offset") else 0,
                    crop_offset_y=cam.offset.y if hasattr(cam, "offset") else 0,
                    crop_width=200,
                    crop_height=200,
                    gain=getattr(cam, "gain", 0),
                    chroma="Monochrome",
                    bit_depth=16,
                )
            )
        return detectors

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
                name="Blue excitation filter",
                manufacturer=devices.Organization.SEMROCK,
                model="FF01-473/10-25",
                filter_type="Band pass",
                center_wavelength=473,
            ),
        ]

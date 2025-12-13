import logging
import socket
import sys
from datetime import date
from typing import Optional, List

import aind_data_schema.components.devices as devices
import aind_data_schema.core.instrument as instrument
from aind_data_schema.components.connections import Connection
from aind_data_schema.components.devices import Computer
from aind_data_schema_models.modalities import Modality

from aind_physiology_fip.rig import AindPhysioFipRig

logger = logging.getLogger(__name__)

def _get_fiber_patch_cords() -> List[devices.FiberPatchCord]:
    """
    Retrieve the list of fiber patch cords for the instrument model.

    Returns
    -------
    List[devices.FiberPatchCord]
        A list of FiberPatchCord objects associated with the instrument.
    """
    patch_cord_note = (
        "All four patch cords are actually a single device at the camera end "
        "with four separate connections to connect to up to four implanted fibers. "
        "Unused patch cables are not physically connected to an implanted fiber during an experiment."
    )

    patch_cord_0 = devices.FiberPatchCord(
        name="Patch Cord 0",
        manufacturer=devices.Organization.DORIC,
        model="BBP(4)_200/220/900-0.37_Custom_FCM-4xMF1.25",
        core_diameter=200,
        numerical_aperture=0.37,
        notes=patch_cord_note,
    )

    patch_cord_1 = devices.FiberPatchCord(
        name="Patch Cord 1",
        manufacturer=devices.Organization.DORIC,
        model="BBP(4)_200/220/900-0.37_Custom_FCM-4xMF1.25",
        core_diameter=200,
        numerical_aperture=0.37,
        notes=patch_cord_note,
    )

    patch_cord_2 = devices.FiberPatchCord(
        name="Patch Cord 2",
        manufacturer=devices.Organization.DORIC,
        model="BBP(4)_200/220/900-0.37_Custom_FCM-4xMF1.25",
        core_diameter=200,
        numerical_aperture=0.37,
        notes=patch_cord_note,
    )

    patch_cord_3 = devices.FiberPatchCord(
        name="Patch Cord 3",
        manufacturer=devices.Organization.DORIC,
        model="BBP(4)_200/220/900-0.37_Custom_FCM-4xMF1.25",
        core_diameter=200,
        numerical_aperture=0.37,
        notes=patch_cord_note,
    )

    return [patch_cord_0, patch_cord_1, patch_cord_2, patch_cord_3]


def map_to_aind_data_schema_instrument(
    rig_input: AindPhysioFipRig,
) -> instrument.Instrument:
    """
    Map a physiology FIP rig description to an AIND data schema Instrument.

    Parameters
    ----------
    rig_input : AindPhysioFipRig
        Input rig configuration describing the physiology FIP setup.

    Returns
    -------
    instrument.Instrument
        Instrument object conforming to the AIND data schema.
    """

    computer_name = socket.gethostname() # is this correct?
    computer = Computer(name=computer_name)

    patch_coords = _get_fiber_patch_cords()

    light_source_uv = devices.LightEmittingDiode(
        name=rig_input.light_source_uv.name,
        manufacturer=devices.Organization.THORLABS,
        model="M470F3",
        wavelength=int(rig_input.light_source_uv.power)
    )
    light_source_blue = devices.LightEmittingDiode(
        name=rig_input.light_source_blue.name,
        manufacturer=devices.Organization.THORLABS,
        model="M415F3",
        wavelength=int(rig_input.light_source_blue.power)
    )
    light_source_lime = devices.LightEmittingDiode(
        name=rig_input.light_source_lime.name,
        manufacturer=devices.Organization.THORLABS,
        model="M565F3",
        wavelength=int(rig_input.light_source_lime.power)
    )

    detector_red = devices.Detector(
        name=rig_input.camera_red.name,
        serial_number=rig_input.camera_red.serial_number,
        model="BFS-U3-20S40M",
        detector_type="Camera",
        data_interface="USB",
        cooling="Air",
        immersion="air",
        bin_width=4,
        bin_height=4,
        bin_mode="Additive",
        crop_offset_x=rig_input.camera_red.offset.x,
        crop_offset_y=rig_input.camera_red.offset.y,
        crop_width=200,
        crop_height=200,
        gain=rig_input.camera_red.gain,
        chroma="Monochrome",
        bit_depth=16,

    )
    




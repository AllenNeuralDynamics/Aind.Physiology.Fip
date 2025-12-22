"""Utility constants for rigs and device configurations."""

import enum
from dataclasses import dataclass
from typing import List, Optional, Union

import aind_data_schema.components.devices as devices

# -------------------------------
# Device constants
# -------------------------------


class TrackedDevicesInfo(enum.Enum):
    # Computer
    COMPUTER = "computer"

    # Lens
    LENS_NAME = "Image focusing lens"
    LENS_MODEL = "AC254-080-A-ML"

    # Clock
    WHITE_RABBIT_DEVICE_NAME = "harp_clock_generator"

    # Detector
    DETECTOR_BIN_WIDTH = 4
    DETECTOR_BIN_HEIGHT = 4
    DETECTOR_MODEL = "BFS-U3-20S40M"
    DETECTOR_CROP_WIDTH = 200
    DETECTOR_CROP_HEIGHT = 200
    DETECTOR_BIT_DEPTH = 16

    # Ports
    PORT_COM = "COM14"
    PORT_CLOCK = "ClkOut"

    # Patch cord
    PATCH_CORD_MODEL = "BBP(4)_200/220/900-0.37_Custom_FCM-4xMF1.25"
    PATCH_CORD_DIAMETER = 200
    PATCH_CORD_NUMERICAL_APERTURE = 0.37

    # Objective
    OBJECTIVE_MODEL = "CFI Plan Apochromat Lambda D 10x"
    OBJECTIVE_NUMERICAL_APERTURE = 0.45
    OBJECTIVE_SERIAL_NUMBER = "12812388"
    OBJECTIVE_MAGNIFICATION = 10


# -------------------------------
# Filters
# -------------------------------


@dataclass(frozen=True)
class FilterSpec:
    name: str
    manufacturer: devices.Organization
    model: str
    filter_type: str
    center_wavelength: Optional[Union[int, List[int]]] = None
    cut_off_wavelength: Optional[int] = None
    notes: Optional[str] = None


class FilterId(enum.Enum):
    GREEN_EMISSION = enum.auto()
    RED_EMISSION = enum.auto()

    EMISSION_DICHROIC = enum.auto()
    DUAL_EDGE_DICHROIC = enum.auto()

    EXCITATION_410 = enum.auto()
    EXCITATION_470 = enum.auto()
    EXCITATION_560 = enum.auto()

    DICHROIC_450_LONGPASS = enum.auto()
    DICHROIC_500_LONGPASS = enum.auto()


FILTER_SPECS: dict[FilterId, FilterSpec] = {
    FilterId.GREEN_EMISSION: FilterSpec(
        name="Green emission filter",
        manufacturer=devices.Organization.SEMROCK,
        model="FF01-520/35-25",
        filter_type="Band pass",
        center_wavelength=520,
    ),
    FilterId.RED_EMISSION: FilterSpec(
        name="Red emission filter",
        manufacturer=devices.Organization.SEMROCK,
        model="FF01-600/37-25",
        filter_type="Band pass",
        center_wavelength=600,
    ),
    FilterId.EMISSION_DICHROIC: FilterSpec(
        name="Emission Dichroic",
        manufacturer=devices.Organization.SEMROCK,
        model="FF562-Di03-25x36",
        filter_type="Dichroic",
        cut_off_wavelength=562,
    ),
    FilterId.DUAL_EDGE_DICHROIC: FilterSpec(
        name="dual-edge standard epi-fluorescence dichroic beamsplitter",
        manufacturer=devices.Organization.SEMROCK,
        model="FF493/574-Di01-25x36",
        filter_type="Multiband",
        center_wavelength=[493, 574],
        notes="BrightLine dual-edge standard epi-fluorescence dichroic beamsplitter",
    ),
    FilterId.EXCITATION_410: FilterSpec(
        name="Excitation filter 410nm",
        manufacturer=devices.Organization.THORLABS,
        model="FB410-10",
        filter_type="Band pass",
        center_wavelength=410,
    ),
    FilterId.EXCITATION_470: FilterSpec(
        name="Excitation filter 470nm",
        manufacturer=devices.Organization.THORLABS,
        model="FB470-10",
        filter_type="Band pass",
        center_wavelength=470,
    ),
    FilterId.EXCITATION_560: FilterSpec(
        name="Excitation filter 560nm",
        manufacturer=devices.Organization.THORLABS,
        model="FB560-10",
        filter_type="Band pass",
        center_wavelength=560,
    ),
    FilterId.DICHROIC_450_LONGPASS: FilterSpec(
        name="450 Dichroic Longpass Filter",
        manufacturer=devices.Organization.EDMUND_OPTICS,
        model="#69-898",
        filter_type="Dichroic",
        cut_off_wavelength=450,
    ),
    FilterId.DICHROIC_500_LONGPASS: FilterSpec(
        name="500 Dichroic Longpass Filter",
        manufacturer=devices.Organization.EDMUND_OPTICS,
        model="#69-899",
        filter_type="Dichroic",
        cut_off_wavelength=500,
    ),
}


def make_filter(filter_id: FilterId) -> devices.Filter:
    """
    Create an aind-data-schema Filter from a canonical FilterId.
    """
    spec = FILTER_SPECS[filter_id]
    return devices.Filter(**spec.__dict__)

"""Utility constants and factories for rigs and device configurations."""

import enum
from typing import NamedTuple

from aind_data_schema.components import devices
from aind_data_schema_models import units

# -------------------------------
# Device constants
# -------------------------------


class TrackedDeviceName(enum.StrEnum):
    """Names of the devices that the rig schema does not track.

    ``aind_behavior_services.rig.Device`` carries no ``name`` field, so the instrument
    metadata has to name these devices manually. The values must match the reference FIP
    instrument, since they are the identifiers that ``Connection`` objects refer to.
    """

    COMPUTER = "computer"
    CLOCK_GENERATOR = "harp_clock_generator"
    CUTTLEFISH = "cuTTLefishFip"
    CAMERA_GREEN_ISO = "Green CMOS"
    CAMERA_RED = "Red CMOS"
    LED_UV = "415nm LED"
    LED_BLUE = "470nm LED"
    LED_LIME = "565nm LED"
    LENS = "Image focusing lens"
    OBJECTIVE = "Objective"


class TrackedDevicesInfo:
    # Lens
    LENS_MODEL = "AC254-080-A-ML"

    # Detector
    DETECTOR_BIN_WIDTH = 4
    DETECTOR_BIN_HEIGHT = 4
    DETECTOR_MODEL = "BFS-U3-20S40M"
    DETECTOR_CROP_WIDTH = 200
    DETECTOR_CROP_HEIGHT = 200
    DETECTOR_BIT_DEPTH = 16

    # Ports
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


FILTERS: dict[FilterId, devices.Filter] = {
    FilterId.GREEN_EMISSION: devices.Filter(
        name="Green emission filter",
        manufacturer=devices.Organization.SEMROCK,
        model="FF01-520/35-25",
        filter_type="Band pass",
        center_wavelength=520,
    ),
    FilterId.RED_EMISSION: devices.Filter(
        name="Red emission filter",
        manufacturer=devices.Organization.SEMROCK,
        model="FF01-600/37-25",
        filter_type="Band pass",
        center_wavelength=600,
    ),
    FilterId.EMISSION_DICHROIC: devices.Filter(
        name="Emission Dichroic",
        manufacturer=devices.Organization.SEMROCK,
        model="FF562-Di03-25x36",
        filter_type="Dichroic",
        cut_off_wavelength=562,
    ),
    FilterId.DUAL_EDGE_DICHROIC: devices.Filter(
        name="dual-edge standard epi-fluorescence dichroic beamsplitter",
        manufacturer=devices.Organization.SEMROCK,
        model="FF493/574-Di01-25x36",
        filter_type="Multiband",
        center_wavelength=[493, 574],
        notes="BrightLine dual-edge standard epi-fluorescence dichroic beamsplitter",
    ),
    FilterId.EXCITATION_410: devices.Filter(
        name="Excitation filter 410nm",
        manufacturer=devices.Organization.THORLABS,
        model="FB410-10",
        filter_type="Band pass",
        center_wavelength=410,
    ),
    FilterId.EXCITATION_470: devices.Filter(
        name="Excitation filter 470nm",
        manufacturer=devices.Organization.THORLABS,
        model="FB470-10",
        filter_type="Band pass",
        center_wavelength=470,
    ),
    FilterId.EXCITATION_560: devices.Filter(
        name="Excitation filter 560nm",
        manufacturer=devices.Organization.THORLABS,
        model="FB560-10",
        filter_type="Band pass",
        center_wavelength=560,
    ),
    FilterId.DICHROIC_450_LONGPASS: devices.Filter(
        name="450 Dichroic Longpass Filter",
        manufacturer=devices.Organization.EDMUND_OPTICS,
        model="#69-898",
        filter_type="Dichroic",
        cut_off_wavelength=450,
    ),
    FilterId.DICHROIC_500_LONGPASS: devices.Filter(
        name="500 Dichroic Longpass Filter",
        manufacturer=devices.Organization.EDMUND_OPTICS,
        model="#69-899",
        filter_type="Dichroic",
        cut_off_wavelength=500,
    ),
}


def make_filter(filter_id: FilterId) -> devices.Filter:
    """Return the devices.Filter for a given FilterId."""
    return FILTERS[filter_id]


# -------------------------------
# LEDs
# -------------------------------


class LedId(enum.Enum):
    UV = enum.auto()
    BLUE = enum.auto()
    LIME = enum.auto()


class LedSpec(NamedTuple):
    """Fixed properties of one of the rig's LEDs.

    The Thorlabs part numbers encode the emission wavelength, so ``model`` and
    ``wavelength`` always agree (e.g. ``M415F3`` emits at 415 nm).
    """

    name: TrackedDeviceName
    model: str
    wavelength: int


LED_SPECS: dict[LedId, LedSpec] = {
    LedId.UV: LedSpec(TrackedDeviceName.LED_UV, "M415F3", 415),
    LedId.BLUE: LedSpec(TrackedDeviceName.LED_BLUE, "M470F3", 470),
    LedId.LIME: LedSpec(TrackedDeviceName.LED_LIME, "M565F3", 565),
}


def make_led(color: LedId) -> devices.LightEmittingDiode:
    """Construct a LightEmittingDiode from a color enum."""
    spec = LED_SPECS[color]
    return devices.LightEmittingDiode(
        name=spec.name,
        manufacturer=devices.Organization.THORLABS,
        model=spec.model,
        wavelength=spec.wavelength,
        wavelength_unit=units.SizeUnit.NM,
    )

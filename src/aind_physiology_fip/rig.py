from typing import Annotated, Dict, List, Literal, Optional, Self

from aind_behavior_services import calibration, rig
from pydantic import BaseModel, Field, model_validator

__version__ = "0.1.0"


class Point2f(BaseModel):
    x: float = Field(description="X coordinate of the point (px)")
    y: float = Field(description="Y coordinate of the point (px)")


class Circle(BaseModel):
    center: Point2f = Field(default=Point2f(x=0, y=0), description="Center of the circle (px)", validate_default=True)
    radius: float = Field(default=50, ge=0, description="Radius of the circle (px)")


class FipCamera(rig.Device):
    device_type: Literal["FipCamera"] = "FipCamera"
    serial_number: str = Field(..., description="Camera serial number")
    gain: float = Field(default=0, ge=0, description="Gain")
    offset: Point2f = Field(default=Point2f(x=0, y=0), description="Offset (px)", validate_default=True)


def _make_default_rois() -> List[Circle]:
    return [Circle(center=Point2f(x=x, y=y), radius=20) for x in (50, 150) for y in (50, 150)]


class RoiSettings(BaseModel):
    camera_green_iso_background: Circle = Field(
        default=Circle(center=Point2f(x=10, y=10), radius=10),
        description="ROI to compute the background for the green/iso camera channel",
    )
    camera_red_background: Circle = Field(
        default=Circle(center=Point2f(x=10, y=10), radius=10),
        description="ROI to compute the background for the red camera channel",
    )
    camera_green_iso_roi: List[Circle] = Field(
        default=_make_default_rois(), description="ROI for the green/iso camera channel"
    )
    camera_red_roi: List[Circle] = Field(default=_make_default_rois(), description="ROI for the red camera channel")


class HarpCuttlefishFip(rig.harp._HarpDeviceBase):
    device_type: Literal["cuTTLefishFip"] = "cuTTLefishFip"
    who_am_i: Literal[1407] = 1407


class ZmqConnection(BaseModel):
    connection_string: str = Field(default="@tcp://localhost:5556")
    topic: str = Field(default="")


class Networking(BaseModel):
    zmq_publisher: ZmqConnection = Field(
        default=ZmqConnection(connection_string="@tcp://localhost:5556", topic="fip"), validate_default=True
    )
    zmq_subscriber: ZmqConnection = Field(
        default=ZmqConnection(connection_string="@tcp://localhost:5557", topic="fip"), validate_default=True
    )


LightSourcePower = Annotated[float, Field(default=0, ge=0, description="Power (mW)")]
DutyCycle = Annotated[float, Field(default=0, ge=0, le=100, description="Duty cycle (0-100%)")]


class LightSourceCalibrationOutput(BaseModel):
    power_lut: Dict[DutyCycle, LightSourcePower] = Field(
        ..., description="Look-up table for LightSource power vs. duty cycle"
    )


class LightSourceCalibration(calibration.Calibration):
    output: LightSourceCalibrationOutput = Field(..., title="Lookup table to convert duty cycle to power (mW)")


class LightSource(rig.Device):
    device_type: Literal["LightSource"] = "LightSource"
    power: float = Field(default=0, ge=0, description="Power (mW)")
    calibration: Optional[LightSourceCalibration] = Field(
        default=None,
        title="Calibration",
        description="Calibration for the LightSource. If left empty, 'power' will be used as duty-cycle (0-100).",
    )

    @model_validator(mode="after")
    def _validate_power(self) -> Self:
        if self.calibration is None:
            if self.power < 0 or self.power > 100:
                raise ValueError("Power must be between 0 and 100 when no calibration is provided.")
        return self


class AindPhysioFipRig(rig.AindBehaviorRigModel):
    version: Literal[__version__] = __version__
    camera_green_iso: FipCamera = Field(title="G/Iso Camera", description="Camera for the green and iso channels")
    camera_red: FipCamera = Field(title="Red Camera", description="Red camera")
    light_source_uv: LightSource = Field(title="UV light source", description="UV (415nm) light source")
    light_source_blue: LightSource = Field(title="Blue light source", description="Blue (470nm) light source")
    light_source_lime: LightSource = Field(title="Lime light source", description="Lime (560nm) light source")
    roi_settings: Optional[RoiSettings] = Field(
        default=None,
        title="Region of interest settings",
        description="Region of interest settings. Leave empty to attempt to load from local file or manually define it in the program.",
    )
    cuttlefish_fip: HarpCuttlefishFip = Field(
        title="CuttlefishFip",
        description="CuttlefishFip board for controlling the trigger of cameras and light-sources",
    )
    networking: Networking = Field(Networking(), title="Networking", description="Networking settings")

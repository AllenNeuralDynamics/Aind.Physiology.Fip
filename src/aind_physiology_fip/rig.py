from typing import List, Literal, Optional

from aind_behavior_services import rig
from pydantic import BaseModel, Field

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

class RoiSettings(BaseModel):
    camera_green_iso: Circle = Field(description="Region of interest to be applied to the green and iso camera channel")
    camera_red: Circle = Field(description="Region of interest to be applied to the red camera channel")
    operation: Literal["Avg"] = Field(default="Avg", description="Operation to be applied to the region of interest")


class HarpCuttlefishFipSettings(BaseModel):
    green_light_source_duty_cycle: int = Field(default=1, ge=0, le=100, description="Green light source power (0-100%)")
    red_light_source_duty_cycle: int = Field(default=1, ge=0, le=100, description="Red light source power (0-100%)")
    # TODO light source tasks...


class HarpCuttlefishFip(rig.harp._HarpDeviceBase):
    device_type: Literal["cuTTLefishFip"] = "cuTTLefishFip"
    who_am_i: Literal[1407] = 1407
    additional_settings: HarpCuttlefishFipSettings = Field(
        description="Additional settings for the cuTTLefishFip device"
    )


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


class AindPhysioFipRig(rig.AindBehaviorRigModel):
    version: Literal[__version__] = __version__
    camera_green_iso: FipCamera = Field(title="G/Iso Camera", description="Camera for the green and iso channels")
    camera_red: FipCamera = Field(title="Red Camera", description="Red camera")
    roi_settings: List[RoiSettings] = Field(
        default=[], title="Region of interest settings", description="Region of interest settings"
    )
    cuttlefish_fip: HarpCuttlefishFip = Field(
        title="CuttlefishFip",
        description="CuttlefishFip board for controlling the trigger of cameras and light-sources",
    )
    networking: Networking = Field(Networking(), title="Networking", description="Networking settings")

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


class VideoMatrixWriter(BaseModel):
    video_writer_type: Literal["VideoMatrixWriter"] = Field(default="VideoMatrixWriter")
    container_extension: str = Field(default="bin", description="Container extension")
    layout: Literal["RawMajor", "ColumnMajor"] = Field(default="ColumnMajor", description="Layout of the video matrix")
    spatial_downsample: int = Field(default=1, ge=1, description="Downsample factor")


class FipCamera(rig.cameras.SpinnakerCamera):
    binning: int = Field(default=1, ge=1, description="Binning")  # TODO
    color_processing: Literal["Default", "NoColorProcessing"] = Field(
        default="Default", description="Color processing"
    )  # TODO
    exposure: int = Field(default=1000, ge=100, description="Exposure time")  # TODO
    gain: float = Field(default=0, ge=0, description="Gain")  # TODO
    gamma: Optional[float] = Field(
        default=None, ge=0, description="Gamma. If None, will disable gamma correction."
    )  # TODO
    adc_bit_depth: Literal[rig.cameras.SpinnakerCameraAdcBitDepth.ADC12BIT] = Field(
        default=rig.cameras.SpinnakerCameraAdcBitDepth.ADC12BIT,
        description="ADC bit depth. If None will be left as default.",
    )
    pixel_format: Optional[rig.cameras.SpinnakerCameraPixelFormat] = Field(
        default=rig.cameras.SpinnakerCameraPixelFormat.MONO16,
        description="Pixel format. If None will be left as default.",
    )
    region_of_interest: rig.cameras.Rect = Field(
        default=rig.cameras.Rect(), description="Region of interest", validate_default=True
    )
    video_writer: Optional[VideoMatrixWriter] = Field(
        default=VideoMatrixWriter(),
        description="Video writer. If not provided, no video will be saved.",
        validate_default=True,
    )  # todo fix with https://github.com/AllenNeuralDynamics/Aind.Behavior.Services/issues/155


class RoiSettings(BaseModel):
    camera_green_iso: Circle = Field(description="Region of interest to be applied to the green and iso camera channel")
    camera_red: Circle = Field(description="Region of interest to be applied to the red camera channel")
    operation: Literal["Avg"] = Field(default="Avg", description="Operation to be applied to the region of interest")


class HarpCuttlefishFipSettings(BaseModel):
    green_light_source_power: int = Field(default=0, ge=0, le=100, description="Green light source power (0-100%)")
    red_light_source_power: int = Field(default=0, ge=0, le=100, description="Red light source power (0-100%)")
    # TODO light source tasks...


class HarpCuttlefishFip(rig._harp_gen._HarpDeviceBase):
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

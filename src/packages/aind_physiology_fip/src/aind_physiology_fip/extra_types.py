from aind_behavior_services.common import Circle
from pydantic import BaseModel, Field

from .task_logic import Measurement


class FipCircle(BaseModel):
    """Describes a circle in the FIP system together with the measurement (fiber, ROI, name) it belongs to."""

    circle: Circle = Field(description="Geometry of the circle.")
    measurement: Measurement = Field(description="Measurement (fiber, ROI, name, enabled) associated with the circle.")


class RoiSettings(BaseModel):
    """Region of Interest (ROI) settings for camera channels in the FIP system."""

    camera_green_iso_background: Circle = Field(
        description="ROI to compute the background for the green/iso camera channel",
    )
    camera_red_background: Circle = Field(
        description="ROI to compute the background for the red camera channel",
    )
    camera_green_iso_roi: list[FipCircle] = Field(description="ROI for the green/iso camera channel")
    camera_red_roi: list[FipCircle] = Field(description="ROI for the red camera channel")

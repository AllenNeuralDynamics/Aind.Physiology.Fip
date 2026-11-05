from aind_behavior_services.common import Circle
from pydantic import BaseModel, Field


class FipCircle(Circle):
    """Describes a circle in the FIP system with associated ROI, fiber, and optional name."""

    roi_number: int | None = Field(default=None, ge=0, description="ROI number associated with the circle")
    fiber_number: int | None = Field(default=None, ge=0, description="Fiber number associated with the circle")
    name: str | None = Field(default=None, description="Name associated with the circle")
    enabled: bool = Field(default=True, description="Indicates whether the circle is enabled")


class RoiSettings(BaseModel):
    """Region of Interest (ROI) settings for camera channels in the FIP system."""

    camera_green_iso_background: FipCircle = Field(
        description="ROI to compute the background for the green/iso camera channel",
    )
    camera_red_background: FipCircle = Field(
        description="ROI to compute the background for the red camera channel",
    )
    camera_green_iso_roi: list[FipCircle] = Field(description="ROI for the green/iso camera channel")
    camera_red_roi: list[FipCircle] = Field(description="ROI for the red camera channel")

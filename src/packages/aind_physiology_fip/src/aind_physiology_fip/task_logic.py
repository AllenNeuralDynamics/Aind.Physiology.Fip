from typing import Literal, Self

from aind_behavior_services.task import Task, TaskParameters
from pydantic import BaseModel, Field, model_validator

from . import __semver__
from .rig import LightSourcePower


class Measurement(BaseModel):
    """Description of a single fiber."""

    fiber_number: int = Field(ge=0, description="Fiber number.")
    roi_number: int = Field(ge=0, description="ROI number associated with the fiber.")
    name: str | None = Field(
        default=None, description="Name associated with the ROI. E.g.: target brain area or structure."
    )
    enabled: bool = Field(default=True, description="Whether the fiber is enabled.")


class AindPhysioFipTaskParameters(TaskParameters):
    """Parameters of the FIP task, mirroring what the UI lets the user change."""

    measurements: list[Measurement] = Field(default_factory=list, description="Measurements used in the experiment.")
    blue_power: LightSourcePower | None = Field(
        default=None, validate_default=True, description="Power of the blue light source in uW."
    )
    uv_power: LightSourcePower | None = Field(
        default=None, validate_default=True, description="Power of the UV light source in uW."
    )
    lime_power: LightSourcePower | None = Field(
        default=None, validate_default=True, description="Power of the lime light source in uW."
    )

    @model_validator(mode="after")
    def _validate_unique_fibers(self) -> Self:
        for attr in ("fiber_number", "roi_number"):
            values = [getattr(fiber, attr) for fiber in self.measurements]
            duplicates = sorted({v for v in values if values.count(v) > 1})
            if duplicates:
                raise ValueError(f"Duplicated {attr} found in fibers: {duplicates}")
        return self


class AindPhysioFipTaskLogic(Task):
    """Task logic for the AIND FIP photometry system."""

    name: Literal["AindPhysioFip"] = "AindPhysioFip"
    version: Literal[__semver__] = __semver__
    task_parameters: AindPhysioFipTaskParameters = Field(
        default_factory=AindPhysioFipTaskParameters,
        validate_default=True,
        description="Parameters of the task",
    )

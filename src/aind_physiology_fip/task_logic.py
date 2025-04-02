from typing import Literal

from aind_behavior_services.task_logic import AindBehaviorTaskLogicModel, TaskParameters
from pydantic import Field

__version__ = "0.1.0"


class AindPhysioFipParameters(TaskParameters):
    example_parameter: float = Field(default=0, title="Example parameter", description="This is an example parameter")


class AindPhysioFipTaskLogic(AindBehaviorTaskLogicModel):
    """Olfactometer operation control model that is used to run a calibration data acquisition workflow"""

    name: Literal["AindPhysiologyFip"] = Field(default="AindPhysiologyFip", title="Name of the task logic", frozen=True)
    version: Literal[__version__] = __version__
    task_parameters: AindPhysioFipParameters = Field(..., title="Task parameters", validate_default=True)

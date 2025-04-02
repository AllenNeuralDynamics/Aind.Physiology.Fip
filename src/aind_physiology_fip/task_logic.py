from typing import Literal

from aind_behavior_services.task_logic import AindBehaviorTaskLogicModel, TaskParameters
from pydantic import Field

__version__ = "0.1.0"


class AindPhysioFipParameters(TaskParameters):
    pass


class AindPhysioFipTaskLogic(AindBehaviorTaskLogicModel):
    """Olfactometer operation control model that is used to run a calibration data acquisition workflow"""

    name: Literal["AindPhysiologyFip"] = Field(default="AindPhysiologyFip", title="Name of the task logic", frozen=True)
    version: Literal[__version__] = __version__
    task_parameters: AindPhysioFipParameters = Field(
        AindPhysioFipParameters(), title="Task parameters", validate_default=True
    )

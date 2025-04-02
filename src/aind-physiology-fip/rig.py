from typing import Literal

from aind_behavior_services.rig import AindBehaviorRigModel
from pydantic import Field

__version__ = "0.1.0"


class AindPhysioFipRig(AindBehaviorRigModel):
    version: Literal[__version__] = __version__
    example_parameter: float = Field(default=0, title="Example parameter", description="This is an example parameter")

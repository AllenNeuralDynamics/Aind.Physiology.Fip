import datetime
import os

from aind_behavior_services.session import AindBehaviorSessionModel

from aind_physiology_fip.rig import (
    AindPhysioFipRig,
    Circle,
    FipCamera,
    HarpCuttlefishFip,
    HarpCuttlefishFipSettings,
    Networking,
    Point2f,
    RoiSettings,
)
from aind_physiology_fip.task_logic import AindPhysioFipTaskLogic


def mock_session() -> AindBehaviorSessionModel:
    """Generates a mock AindBehaviorSessionModel model"""
    return AindBehaviorSessionModel(
        date=datetime.datetime.now(tz=datetime.timezone.utc),
        experiment="AindPhysioFip",
        root_path="c://",
        subject="test",
        notes="test session",
        experiment_version="0.0.0",
        allow_dirty_repo=True,
        skip_hardware_validation=False,
        experimenter=["Foo", "Bar"],
    )


def mock_rig() -> AindPhysioFipRig:
    return AindPhysioFipRig(
        rig_name="test_rig",
        camera_green_iso=FipCamera(serial_number="000000"),
        camera_red=FipCamera(serial_number="000001"),
        roi_settings=[
            RoiSettings(
                camera_green_iso=Circle(center=Point2f(x=0, y=0), radius=10),
                camera_red=Circle(center=Point2f(x=0, y=0), radius=10),
            ),
            RoiSettings(
                camera_green_iso=Circle(center=Point2f(x=10, y=10), radius=10),
                camera_red=Circle(center=Point2f(x=10, y=10), radius=10),
            ),
        ],
        networking=Networking(),
        cuttlefish_fip=HarpCuttlefishFip(
            port_name="COM1",
            additional_settings=HarpCuttlefishFipSettings(
                green_light_source_duty_cyle=10, red_light_source_duty_cycle=20
            ),
        ),
    )


def mock_task_logic() -> AindPhysioFipTaskLogic:
    return AindPhysioFipTaskLogic()


def main(path_seed: str = "./local/{schema}.json"):
    example_session = mock_session()
    example_rig = mock_rig()
    example_task_logic = mock_task_logic()

    os.makedirs(os.path.dirname(path_seed), exist_ok=True)

    for model in [example_task_logic, example_session, example_rig]:
        with open(path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2))


if __name__ == "__main__":
    main()

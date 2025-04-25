import datetime
import os

from aind_behavior_services.session import AindBehaviorSessionModel

import aind_physiology_fip.task_logic as task_logic
from aind_physiology_fip.rig import (
    AindPhysioFipRig,
    FipCamera,
    HarpCuttlefishFip,
    LightSource,
    LightSourceCalibration,
    LightSourceCalibrationOutput,
    Networking,
    RoiSettings,
    FipTask,
    Ports,
)


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
    mock_calibration = LightSourceCalibration(
        device_name="mock_device", output=LightSourceCalibrationOutput(power_lut={0: 0, 0.1: 10, 0.2: 20})
    )

    return AindPhysioFipRig(
        rig_name="test_rig",
        camera_green_iso=FipCamera(serial_number="000000"),
        camera_red=FipCamera(serial_number="000001"),
        light_source_blue=LightSource(
            power=10,
            calibration=mock_calibration,
            task=FipTask(
                camera_port=Ports.IO3,
                light_source_port=Ports.IO2,
            ),
        ),
        light_source_lime=LightSource(
            power=20,
            calibration=mock_calibration,
            task=FipTask(
                camera_port=Ports.IO5,
                light_source_port=Ports.IO4,
            ),
        ),
        light_source_uv=LightSource(
            power=5, calibration=None, task=FipTask(camera_port=Ports.IO1, light_source_port=Ports.IO0)
        ),
        roi_settings=RoiSettings(),
        networking=Networking(),
        cuttlefish_fip=HarpCuttlefishFip(
            port_name="COM1",
        ),
    )


def mock_task_logic() -> task_logic.AindPhysioFipTaskLogic:
    return task_logic.AindPhysioFipTaskLogic()


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

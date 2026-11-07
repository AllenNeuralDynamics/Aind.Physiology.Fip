import argparse
import datetime
import os

from aind_behavior_services.common import Circle, Point2f
from aind_behavior_services.rig.harp import HarpCuttlefishfip
from aind_behavior_services.session import Session
from aind_physiology_fip.extra_types import FipCircle, RoiSettings
from aind_physiology_fip.rig import (
    AindPhysioFipRig,
    FipCamera,
    FipTask,
    LightSource,
    LightSourceCalibration,
    Networking,
    Ports,
)
from aind_physiology_fip.task_logic import Measurement


def mock_session() -> Session:
    """Generates a mock Session model"""
    return Session(
        date=datetime.datetime(year=2025, month=1, day=1, hour=12, minute=0, second=0, tzinfo=datetime.UTC),
        experiment="AindPhysioFip",
        subject="test",
        notes="test session",
        allow_dirty_repo=True,
        skip_hardware_validation=False,
        experimenter=["Foo", "Bar"],
    )


def mock_roi_settings() -> RoiSettings:
    """Generates a mock RoiSettings model with four ROIs per camera"""

    def make_rois() -> list[FipCircle]:
        centers = [(x, y) for x in (50, 150) for y in (50, 150)]
        return [
            FipCircle(
                circle=Circle(center=Point2f(x=x, y=y), radius=20),
                measurement=Measurement(roi_number=i, fiber_number=i),
            )
            for i, (x, y) in enumerate(centers)
        ]

    background = Circle(center=Point2f(x=10, y=10), radius=10)
    return RoiSettings(
        camera_green_iso_background=background,
        camera_red_background=background,
        camera_green_iso_roi=make_rois(),
        camera_red_roi=make_rois(),
    )


def mock_rig() -> AindPhysioFipRig:
    mock_calibration = LightSourceCalibration(power_lut={0: 0, 0.1: 10, 0.2: 20})

    return AindPhysioFipRig(
        data_directory=r"C:/data",
        rig_name="test_rig",
        computer_name="test_computer",
        camera_green_iso=FipCamera(serial_number="000000"),
        camera_red=FipCamera(serial_number="000001"),
        light_source_blue=LightSource(
            calibration=mock_calibration,
            task=FipTask(
                camera_port=Ports.IO0,  # GreenCamera + 470nm
                light_source_port=Ports.IO2,
            ),
        ),
        light_source_lime=LightSource(
            calibration=mock_calibration,
            task=FipTask(
                camera_port=Ports.IO1,  # RedCamera + 560nm
                light_source_port=Ports.IO4,
            ),
        ),
        light_source_uv=LightSource(
            calibration=None,
            task=FipTask(
                camera_port=Ports.IO0,  # GreenCamera + 410nm
                light_source_port=Ports.IO3,
            ),
        ),
        networking=Networking(),
        cuttlefish_fip=HarpCuttlefishfip(
            port_name="COM1",
        ),
    )


def main():
    parser = argparse.ArgumentParser(description="Generate mock session and rig JSON files")
    parser.add_argument(
        "--path-seed",
        default="./local/{schema}.json",
        help="Path template for output files (default: ./local/{schema}.json)",
    )
    args = parser.parse_args()

    example_session = mock_session()
    example_rig = mock_rig()

    os.makedirs(os.path.dirname(args.path_seed), exist_ok=True)

    for model in [example_session, example_rig]:
        with open(args.path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2))


if __name__ == "__main__":
    main()

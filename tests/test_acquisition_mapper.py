"""Tests the aind-data-schema acquisition mapper against a synthetic session directory."""

import json
import logging
import sys
from datetime import UTC, datetime
from pathlib import Path
from unittest.mock import patch

import git
import pytest
from aind_data_schema.core import acquisition
from aind_physiology_fip import __semver__
from aind_physiology_fip.extra_types import RoiSettings
from aind_physiology_fip.rig import AindPhysioFipRig
from aind_physiology_fip_data_mappers import AindAcquisitionDataMapper, AindInstrumentDataMapper

sys.path.append(".")
from examples.example import mock_rig, mock_roi_settings, mock_session

# The tests record this checkout's own remote and commit as the acquisition code.
REPOSITORY = Path(__file__).resolve().parents[1]

# What the workflow's LightSourceOutput software event logs: calibrated sources in uW, uncalibrated ones as a duty cycle.
LIGHT_SOURCE_OUTPUT = {
    "blue": {"power": 12.5, "is_calibrated": True},
    "uv": {"power": 0.25, "is_calibrated": False},
    "lime": {"power": 30.0, "is_calibrated": True},
}

# The intended measurements of the mock session's four fibers, as aind-metadata-service returns them for a real subject.
INTENDED_MEASUREMENTS = {
    "Fiber_0": {"G": "serotonin", "Iso": "control", "R": "acetylcholine"},
    "Fiber_1": {"G": "calcium", "Iso": "control", "R": None},
    "Fiber_2": {"G": "dopamine", "Iso": "control", "R": "acetylcholine"},
    "Fiber_3": {"G": "dopamine", "Iso": "control", "R": "acetylcholine"},
}


def _make_session(
    root: Path,
    regions: RoiSettings,
    epoch: str = "fip_2025-01-01T120000",
    rig: AindPhysioFipRig | None = None,
    light_source_output: list[dict] | None = None,
) -> Path:
    """Write one FIP epoch: rig and session inputs, ROIs, session events and frame metadata."""
    epoch_dir = root / "fib" / epoch
    (epoch_dir / "Logs").mkdir(parents=True)
    (epoch_dir / "Logs" / "rig_input.json").write_text((rig or mock_rig()).model_dump_json(), encoding="utf-8")
    (epoch_dir / "Logs" / "session_input.json").write_text(mock_session().model_dump_json(), encoding="utf-8")
    (epoch_dir / "regions.json").write_text(regions.model_dump_json(), encoding="utf-8")

    (epoch_dir / "SoftwareEvents").mkdir()
    for name, timestamp in (
        ("StartSessionTime.json", "2025-01-01T12:00:00+00:00"),
        ("EndSessionTime.json", "2025-01-01T13:00:00+00:00"),
    ):
        (epoch_dir / "SoftwareEvents" / name).write_text(json.dumps({"data": timestamp}) + "\n", encoding="utf-8")

    # One entry per logged output; an empty list leaves the event unwritten, as in sessions that predate it.
    outputs = [LIGHT_SOURCE_OUTPUT] if light_source_output is None else light_source_output
    if outputs:
        lines = "".join(json.dumps({"timestamp": 1000.0 + i, "data": data}) + "\n" for i, data in enumerate(outputs))
        (epoch_dir / "SoftwareEvents" / "LightSourceOutput.json").write_text(lines, encoding="utf-8")

    for channel in ("green", "iso", "red"):
        (epoch_dir / f"{channel}_metadata.json").write_text(
            json.dumps({"Width": 200, "Height": 200, "Depth": "U16"}), encoding="utf-8"
        )
    return root


def _map(root: Path, intended_measurements: dict | None = None) -> acquisition.Acquisition:
    intended_measurements = INTENDED_MEASUREMENTS if intended_measurements is None else intended_measurements
    return AindAcquisitionDataMapper(root, REPOSITORY, intended_measurements=intended_measurements).map()


def _patch_cords(mapped: acquisition.Acquisition) -> dict[str, object]:
    (stream,) = mapped.data_streams
    return {c.device_name: c for c in stream.configurations if c.object_type == "Patch cord config"}


def test_maps_session_to_valid_acquisition(tmp_path):
    """Session details come from the session input, and the result validates as an Acquisition"""
    mapped = _map(_make_session(tmp_path, mock_roi_settings()))

    acquisition.Acquisition.model_validate_json(mapped.model_dump_json())
    assert mapped.subject_id == mock_session().subject
    assert mapped.experimenters == mock_session().experimenter
    assert mapped.notes == mock_session().notes
    assert mapped.instrument_id == mock_rig().rig_name
    assert mapped.acquisition_type == ""


def test_session_end_time_sets_the_acquisition_end(tmp_path):
    """A supplied session end time ends the acquisition, while the stream still ends with its data"""
    end = datetime(2025, 1, 1, 13, 5, tzinfo=UTC)
    mapped = AindAcquisitionDataMapper(
        _make_session(tmp_path, mock_roi_settings()), REPOSITORY, end, intended_measurements=INTENDED_MEASUREMENTS
    ).map()

    assert mapped.acquisition_end_time == end
    (stream,) = mapped.data_streams
    assert stream.stream_end_time == datetime(2025, 1, 1, 13, 0, tzinfo=UTC)


def test_acquisition_ends_with_its_last_stream_by_default(tmp_path):
    """Without a session end time, the acquisition ends when its last stream does"""
    mapped = _map(_make_session(tmp_path, mock_roi_settings()))
    assert mapped.acquisition_end_time == datetime(2025, 1, 1, 13, 0, tzinfo=UTC)


def test_one_patch_cord_per_mapped_roi(tmp_path):
    """ROI N sits over patch cord N, which is connected to the fiber the ROI is mapped to"""
    mapped = _map(_make_session(tmp_path, mock_roi_settings()))
    (stream,) = mapped.data_streams

    assert sorted(_patch_cords(mapped)) == [f"Patch Cord {i}" for i in range(4)]
    assert {(c.source_device, c.target_device) for c in stream.connections} == {
        (f"Patch Cord {i}", f"Fiber_{i}") for i in range(4)
    }


def test_patch_cord_channels(tmp_path):
    """Each patch cord carries a green, isosbestic and red channel, each excited and imaged correctly"""
    patch_cord = _patch_cords(_map(_make_session(tmp_path, mock_roi_settings())))["Patch Cord 0"]
    channels = {
        c.channel_name: (c.light_sources[0].device_name, c.detector.device_name, c.emission_wavelength)
        for c in patch_cord.channels
    }

    assert channels == {
        "Fiber_0_Green": ("470nm LED", "Green CMOS", 520),
        "Fiber_0_Isosbestic": ("415nm LED", "Green CMOS", 520),
        "Fiber_0_Red": ("565nm LED", "Red CMOS", 590),
    }


def test_each_channel_is_exposed_for_its_own_leds_delta_1(tmp_path):
    """Each LED sets the exposure of the frames it excites, so channels on one camera may differ"""
    rig = mock_rig()
    rig.light_source_uv.task.delta_1 = 10000
    rig.light_source_blue.task.delta_1 = 12000
    rig.light_source_lime.task.delta_1 = 14000
    patch_cord = _patch_cords(_map(_make_session(tmp_path, mock_roi_settings(), rig=rig)))["Patch Cord 0"]

    assert {c.channel_name: (c.detector.exposure_time, c.detector.exposure_time_unit) for c in patch_cord.channels} == {
        "Fiber_0_Isosbestic": (10.0, "millisecond"),
        "Fiber_0_Green": (12.0, "millisecond"),
        "Fiber_0_Red": (14.0, "millisecond"),
    }


def _leds(mapped: acquisition.Acquisition) -> dict[str, object]:
    (stream,) = mapped.data_streams
    return {c.device_name: c for c in stream.configurations if c.object_type == "Light emitting diode config"}


def test_led_power_comes_from_the_light_source_output_event(tmp_path):
    """A calibrated LED's power is in microwatts; an uncalibrated one's is a duty cycle, recorded as percent"""
    leds = _leds(_map(_make_session(tmp_path, mock_roi_settings())))

    assert (leds["470nm LED"].power, leds["470nm LED"].power_unit) == (12.5, "microwatt")
    assert (leds["415nm LED"].power, leds["415nm LED"].power_unit) == (pytest.approx(25.0), "percent")
    assert (leds["565nm LED"].power, leds["565nm LED"].power_unit) == (30.0, "microwatt")


def test_led_power_is_the_last_logged_output(tmp_path):
    """The power can change during an epoch, so the last logged output is the one recorded"""
    later = {**LIGHT_SOURCE_OUTPUT, "blue": {"power": 20.0, "is_calibrated": True}}
    leds = _leds(_map(_make_session(tmp_path, mock_roi_settings(), light_source_output=[LIGHT_SOURCE_OUTPUT, later])))

    assert leds["470nm LED"].power == 20.0


def test_epoch_without_light_source_output_is_rejected(tmp_path):
    """Without the event the power the LEDs ran at is unknown, so the epoch is rejected instead of guessed"""
    with pytest.raises(ValueError, match="light source output"):
        _map(_make_session(tmp_path, mock_roi_settings(), light_source_output=[]))


def test_invalid_light_source_output_is_rejected(tmp_path):
    """An output that does not describe every light source is rejected"""
    with pytest.raises(ValueError, match="light source output"):
        _map(_make_session(tmp_path, mock_roi_settings(), light_source_output=[{"blue": LIGHT_SOURCE_OUTPUT["blue"]}]))


def test_disabled_roi_is_not_recorded(tmp_path):
    """An ROI the operator disabled gets no patch cord"""
    regions = mock_roi_settings()
    regions.camera_green_iso_roi[1].measurement.enabled = False
    regions.camera_red_roi[1].measurement.enabled = False

    assert sorted(_patch_cords(_map(_make_session(tmp_path, regions)))) == [
        "Patch Cord 0",
        "Patch Cord 2",
        "Patch Cord 3",
    ]


def test_epoch_without_a_mapped_fiber_is_rejected(tmp_path):
    """A fiber photometry stream needs a patch cord, so an epoch whose ROIs are all disabled is rejected"""
    regions = mock_roi_settings()
    for roi in (*regions.camera_green_iso_roi, *regions.camera_red_roi):
        roi.measurement.enabled = False

    with pytest.raises(ValueError, match="mapped to a fiber"):
        _map(_make_session(tmp_path, regions))


def test_cameras_must_agree_on_an_rois_fiber(tmp_path):
    """ROI N describes one patch cord seen by both cameras, so its fiber cannot differ between them"""
    regions = mock_roi_settings()
    regions.camera_red_roi[0].measurement.fiber_number = 9

    with pytest.raises(ValueError, match="ROI 0"):
        _map(_make_session(tmp_path, regions))


def test_two_rois_cannot_share_a_fiber(tmp_path):
    """Two patch cords cannot both be connected to one implanted fiber"""
    regions = mock_roi_settings()
    for rois in (regions.camera_green_iso_roi, regions.camera_red_roi):
        rois[1].measurement.fiber_number = 0

    with pytest.raises(ValueError, match="same fiber"):
        _map(_make_session(tmp_path, regions))


def test_code_records_the_repository(tmp_path):
    """The Bonsai workflow and the Python package are recorded at the checkout's commit"""
    (stream,) = _map(_make_session(tmp_path, mock_roi_settings())).data_streams
    code = {c.language: c for c in stream.code}
    head = git.Repo(REPOSITORY).head.commit.hexsha

    assert set(code) == {"Bonsai", "Python"}
    assert all(c.version == __semver__ and c.commit_hash == head for c in code.values())
    assert code["Bonsai"].run_script == Path("src/main.bonsai")
    assert code["Bonsai"].language_version is not None


def test_active_devices_exist_in_the_instrument(tmp_path):
    """Every device the acquisition names is an instrument component, except fibers, which are implanted"""
    root = _make_session(tmp_path, mock_roi_settings())
    instrument = AindInstrumentDataMapper(root, clock_generator_name="harp_clock_generator").map()
    (stream,) = _map(root).data_streams

    components = {c.name for c in instrument.components}
    missing = {d for d in stream.active_devices if not d.startswith("Fiber_")} - components
    assert not missing, f"Acquisition names devices the instrument does not have: {sorted(missing)}"


def _intended_measurements(patch_cord) -> dict[str, str | None]:
    return {c.channel_name: c.intended_measurement for c in patch_cord.channels}


def test_channels_record_their_fibers_intended_measurement(tmp_path):
    """Each channel records what its fiber was implanted to measure on that channel"""
    patch_cords = _patch_cords(_map(_make_session(tmp_path, mock_roi_settings())))

    assert _intended_measurements(patch_cords["Patch Cord 0"]) == {
        "Fiber_0_Green": "serotonin",
        "Fiber_0_Isosbestic": "control",
        "Fiber_0_Red": "acetylcholine",
    }
    assert _intended_measurements(patch_cords["Patch Cord 1"])["Fiber_1_Red"] is None


def test_intended_measurement_follows_the_fiber_not_the_roi(tmp_path):
    """A patch cord over ROI N connected to fiber M records fiber M's measurements"""
    regions = mock_roi_settings()
    for rois in (regions.camera_green_iso_roi, regions.camera_red_roi):
        rois[0].measurement.fiber_number, rois[2].measurement.fiber_number = 2, 0

    patch_cord = _patch_cords(_map(_make_session(tmp_path, regions)))["Patch Cord 0"]
    assert _intended_measurements(patch_cord) == {
        "Fiber_2_Green": "dopamine",
        "Fiber_2_Isosbestic": "control",
        "Fiber_2_Red": "acetylcholine",
    }


def test_fiber_without_intended_measurements_is_left_out(tmp_path, caplog):
    """A recorded fiber the subject has no measurements for loses its patch cord, devices and connection"""
    intended_measurements = {k: v for k, v in INTENDED_MEASUREMENTS.items() if k != "Fiber_1"}
    with caplog.at_level(logging.WARNING, logger="aind_physiology_fip_data_mappers._acquisition"):
        mapped = _map(_make_session(tmp_path, mock_roi_settings()), intended_measurements)
    (stream,) = mapped.data_streams

    assert sorted(_patch_cords(mapped)) == ["Patch Cord 0", "Patch Cord 2", "Patch Cord 3"]
    assert not {"Patch Cord 1", "Fiber_1"} & set(stream.active_devices)
    assert ("Patch Cord 1", "Fiber_1") not in {(c.source_device, c.target_device) for c in stream.connections}
    assert "no intended measurements for Fiber_1" in caplog.text


def test_unrecorded_fiber_with_intended_measurements_adds_nothing(tmp_path):
    """Measurements for a fiber no enabled ROI recorded do not add a patch cord or device"""
    intended_measurements = {**INTENDED_MEASUREMENTS, "Fiber_7": {"G": "dopamine", "Iso": "control", "R": None}}
    mapped = _map(_make_session(tmp_path, mock_roi_settings()), intended_measurements)
    (stream,) = mapped.data_streams

    assert sorted(_patch_cords(mapped)) == [f"Patch Cord {i}" for i in range(4)]
    assert "Fiber_7" not in stream.active_devices


def test_epoch_without_a_measured_fiber_is_rejected(tmp_path):
    """With no recorded fiber left to describe, the stream would have no patch cord, so mapping fails"""
    intended_measurements = {"Fiber_7": {"G": "dopamine", "Iso": "control", "R": None}}

    with pytest.raises(ValueError, match="intended measurements"):
        _map(_make_session(tmp_path, mock_roi_settings()), intended_measurements)


def test_intended_measurements_are_fetched_when_not_given(tmp_path):
    """Without measurements passed in, the mapper fetches the session subject's from the metadata service"""
    root = _make_session(tmp_path, mock_roi_settings())
    with patch(
        "aind_physiology_fip_data_mappers._acquisition.fetch_intended_measurements",
        return_value=INTENDED_MEASUREMENTS,
    ) as fetch:
        mapped = AindAcquisitionDataMapper(root, REPOSITORY, metadata_service_url="http://service").map()

    fetch.assert_called_once_with(mock_session().subject, "http://service")
    assert _intended_measurements(_patch_cords(mapped)["Patch Cord 0"])["Fiber_0_Green"] == "serotonin"


def test_mapping_fails_when_intended_measurements_cannot_be_fetched(tmp_path):
    """A failed lookup stops the mapping rather than writing an acquisition without measurements"""
    root = _make_session(tmp_path, mock_roi_settings())
    with (
        patch(
            "aind_physiology_fip_data_mappers._acquisition.fetch_intended_measurements",
            side_effect=ConnectionError("unreachable"),
        ),
        pytest.raises(ConnectionError),
    ):
        AindAcquisitionDataMapper(root, REPOSITORY).map()

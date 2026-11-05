"""Tests the aind-data-schema acquisition mapper against a synthetic session directory."""

import json
import sys
from datetime import UTC, datetime
from pathlib import Path

import git
import pytest
from aind_data_schema.core import acquisition
from aind_physiology_fip import __semver__
from aind_physiology_fip.extra_types import RoiSettings
from aind_physiology_fip_data_mappers import AindAcquisitionDataMapper, AindInstrumentDataMapper

sys.path.append(".")
from examples.example import mock_rig, mock_roi_settings, mock_session

# The tests record this checkout's own remote and commit as the acquisition code.
REPOSITORY = Path(__file__).resolve().parents[1]


def _make_session(root: Path, regions: RoiSettings, epoch: str = "fip_2025-01-01T120000") -> Path:
    """Write one FIP epoch: rig and session inputs, ROIs, session events and frame metadata."""
    epoch_dir = root / "fib" / epoch
    (epoch_dir / "Logs").mkdir(parents=True)
    (epoch_dir / "Logs" / "rig_input.json").write_text(mock_rig().model_dump_json(), encoding="utf-8")
    (epoch_dir / "Logs" / "session_input.json").write_text(mock_session().model_dump_json(), encoding="utf-8")
    (epoch_dir / "regions.json").write_text(regions.model_dump_json(), encoding="utf-8")

    (epoch_dir / "SoftwareEvents").mkdir()
    for name, timestamp in (
        ("StartSessionTime.json", "2025-01-01T12:00:00+00:00"),
        ("EndSessionTime.json", "2025-01-01T13:00:00+00:00"),
    ):
        (epoch_dir / "SoftwareEvents" / name).write_text(json.dumps({"data": timestamp}) + "\n", encoding="utf-8")

    for channel in ("green", "iso", "red"):
        (epoch_dir / f"{channel}_metadata.json").write_text(
            json.dumps({"Width": 200, "Height": 200, "Depth": "U16"}), encoding="utf-8"
        )
    return root


def _map(root: Path) -> acquisition.Acquisition:
    return AindAcquisitionDataMapper(root, REPOSITORY).map()


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
    mapped = AindAcquisitionDataMapper(_make_session(tmp_path, mock_roi_settings()), REPOSITORY, end).map()

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


def test_led_power_is_recorded_in_its_actual_unit(tmp_path):
    """A calibrated LED's power is in microwatts; an uncalibrated one's is a duty cycle, recorded as percent"""
    rig = mock_rig()
    (stream,) = _map(_make_session(tmp_path, mock_roi_settings())).data_streams
    leds = {c.device_name: c for c in stream.configurations if c.object_type == "Light emitting diode config"}

    for name, light_source in (
        ("415nm LED", rig.light_source_uv),
        ("470nm LED", rig.light_source_blue),
        ("565nm LED", rig.light_source_lime),
    ):
        if light_source.calibration is None:
            assert (leds[name].power, leds[name].power_unit) == (pytest.approx(light_source.power * 100), "percent")
        else:
            assert (leds[name].power, leds[name].power_unit) == (light_source.power, "microwatt")


def test_disabled_roi_is_not_recorded(tmp_path):
    """An ROI the operator disabled gets no patch cord"""
    regions = mock_roi_settings()
    regions.camera_green_iso_roi[1].enabled = False
    regions.camera_red_roi[1].enabled = False

    assert sorted(_patch_cords(_map(_make_session(tmp_path, regions)))) == [
        "Patch Cord 0",
        "Patch Cord 2",
        "Patch Cord 3",
    ]


def test_epoch_without_a_mapped_fiber_is_rejected(tmp_path):
    """A fiber photometry stream needs a patch cord, so ROIs with no fiber, as in older sessions, are rejected"""
    regions = mock_roi_settings()
    for roi in (*regions.camera_green_iso_roi, *regions.camera_red_roi):
        roi.fiber_number = None

    with pytest.raises(ValueError, match="mapped to a fiber"):
        _map(_make_session(tmp_path, regions))


def test_cameras_must_agree_on_an_rois_fiber(tmp_path):
    """ROI N describes one patch cord seen by both cameras, so its fiber cannot differ between them"""
    regions = mock_roi_settings()
    regions.camera_red_roi[0].fiber_number = 9

    with pytest.raises(ValueError, match="ROI 0"):
        _map(_make_session(tmp_path, regions))


def test_two_rois_cannot_share_a_fiber(tmp_path):
    """Two patch cords cannot both be connected to one implanted fiber"""
    regions = mock_roi_settings()
    for rois in (regions.camera_green_iso_roi, regions.camera_red_roi):
        rois[1].fiber_number = 0

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

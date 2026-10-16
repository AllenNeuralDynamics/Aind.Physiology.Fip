import pytest
from contraqctor.qc import Status

from aind_physiology_fip.data_qc import FipChannelMetadataTestSuite
from tests.qc_fixtures import CHANNEL_STRIDE, load_stream


def _suite(root, channel="green", **kwargs):
    kwargs.setdefault("frame_stride", CHANNEL_STRIDE[channel])
    return FipChannelMetadataTestSuite(load_stream(root, channel), **kwargs)


@pytest.mark.parametrize("channel", ["green", "iso", "red"])
def test_no_dropped_frames_on_nominal_data(session_factory, channel):
    """Nominal metadata passes for every channel, using each channel's frame stride."""
    assert _suite(session_factory(), channel).test_check_dropped_frames().status == Status.PASSED


@pytest.mark.parametrize("channel", ["green", "red"])
def test_dropped_frame_is_detected(session_factory, channel):
    """A skipped camera frame number fails and the message counts the dropped frames."""
    result = _suite(session_factory(drop_frame_at=40), channel).test_check_dropped_frames()
    assert result.status == Status.FAILED
    assert "1 dropped frames" in result.message


@pytest.mark.parametrize("drift", [1e-3, -1e-3])
def test_clock_drift_is_detected_in_either_direction(session_factory, drift):
    """A camera clock running fast or slow relative to the reference clock fails."""
    result = _suite(session_factory(clock_drift_s=drift)).test_check_dropped_frames()
    assert result.status == Status.FAILED
    assert "CameraFrameTime and ReferenceTime" in result.message


def test_small_clock_drift_is_tolerated(session_factory):
    """Clock drift below the jitter tolerance passes."""
    result = _suite(session_factory(clock_drift_s=1e-5)).test_check_dropped_frames()
    assert result.status == Status.PASSED


def test_fps_is_skipped_when_not_provided(session_factory):
    """No expected FPS means the check is skipped."""
    assert _suite(session_factory()).test_match_expected_fps().status == Status.SKIPPED


def test_fps_matches_expected(session_factory):
    """A 20 Hz channel passes and the message reports the measured mean period."""
    result = _suite(session_factory(), expected_fps=20).test_match_expected_fps()
    assert result.status == Status.PASSED
    assert "Mean frame period (0.05" in result.message  # reports the measured mean, not the error


def test_fps_mismatch_fails(session_factory):
    """A 19 Hz channel fails against an expected 20 Hz, reporting the measured period."""
    result = _suite(session_factory(fps=19.0), expected_fps=20).test_match_expected_fps()
    assert result.status == Status.FAILED
    assert "different than expected" in result.message
    assert "Mean frame period (0.052" in result.message


def test_noisy_frame_period_fails(session_factory):
    """Excess jitter in the frame period fails with a high-std message."""
    result = _suite(session_factory(period_jitter_s=5e-3), expected_fps=20).test_match_expected_fps()
    assert result.status == Status.FAILED
    assert "High std" in result.message

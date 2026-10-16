import numpy as np
import pytest
from aind_behavior_services.common import Circle, Point2f
from contraqctor.qc import Status

from aind_physiology_fip.data_qc import FipRawImageTestSuite
from tests.qc_fixtures import REAL_SESSION_HEAD, load_stream


def _suite(root, channel="green", camera="green_iso", **kwargs):
    regions = load_stream(root, "regions").data
    return FipRawImageTestSuite(
        load_stream(root, f"raw_{channel}"),
        load_stream(root, channel),
        getattr(regions, f"camera_{camera}_background"),
        getattr(regions, f"camera_{camera}_roi"),
        **kwargs,
    )


def _circle(x, y, r):
    return Circle(center=Point2f(x=x, y=y), radius=r)


def test_get_pixels_in_circle_selects_disk():
    """Selects exactly the lattice points inside the circle and returns a mask shaped like the frame."""
    array = np.arange(100).reshape(10, 10)
    pixels, mask = FipRawImageTestSuite._get_pixels_in_circle(array, _circle(5, 5, 2))
    assert mask.shape == array.shape
    assert pixels.size == mask.sum() == 13  # integer lattice points with dx^2 + dy^2 <= 4
    assert array[5, 5] in pixels


def test_get_pixels_in_circle_partially_outside_frame():
    """A circle that extends past the frame is clipped rather than raising."""
    pixels, _ = FipRawImageTestSuite._get_pixels_in_circle(np.ones((10, 10)), _circle(0, 0, 3))
    assert 0 < pixels.size < 29  # clipped by the frame, no exception


def test_render_roi_preserves_shape():
    """Drawing a ROI keeps the image shape and actually changes pixels."""
    image = np.zeros((50, 50, 3), dtype=np.uint8)
    out = FipRawImageTestSuite._render_roi(image, _circle(25, 25, 10), "x")
    assert out.shape == (50, 50, 3)
    assert out.any()


@pytest.mark.parametrize("pixels", [np.array([]), np.zeros(5)])
def test_cv_is_infinite_when_undefined(pixels):
    """An empty or zero-mean ROI yields an infinite CV so it fails the threshold."""
    assert FipRawImageTestSuite._coefficient_of_variation(pixels) == float("inf")


def test_cv_value():
    """The coefficient of variation is std/mean."""
    assert FipRawImageTestSuite._coefficient_of_variation(np.array([1.0, 3.0])) == pytest.approx(0.5)


def test_frame_count_matches(session_factory):
    """Raw frame count equal to the CSV row count passes."""
    assert _suite(session_factory()).test_frame_count().status == Status.PASSED


def test_frame_count_mismatch(session_factory):
    """Fewer raw frames than CSV rows fails and the message reports both counts."""
    result = _suite(session_factory(raw_frames=95)).test_frame_count()
    assert result.status == Status.FAILED
    assert "95 frames in raw data, 100 frames" in result.message


def test_roi_selection_passes_on_uniform_rois(session_factory):
    """Uniform ROIs pass and metrics are reported for the background and each ROI."""
    result = _suite(session_factory()).test_roi_selection()
    assert result.status == Status.PASSED
    assert set(result.context["metrics"]) == {"background_cv"} | {f"roi_{i}_cv" for i in range(4)}


def test_roi_selection_fails_on_noisy_rois(session_factory):
    """ROIs with a high coefficient of variation fail."""
    result = _suite(session_factory(roi_noise=0.3)).test_roi_selection()
    assert result.status == Status.FAILED
    assert "High coefficient of variation" in result.message


def test_roi_selection_respects_cv_threshold(session_factory):
    """The same data passes or fails depending on the configured CV threshold."""
    root = session_factory(roi_noise=0.1)
    assert _suite(root, cv_threshold=0.5).test_roi_selection().status == Status.PASSED
    assert _suite(root, cv_threshold=0.01).test_roi_selection().status == Status.FAILED


def test_roi_selection_fails_on_blank_frames(session_factory):
    """An all-zero frame fails instead of silently passing through NaN comparisons."""
    result = _suite(session_factory(blank_frames=True)).test_roi_selection()
    assert result.status == Status.FAILED


@pytest.mark.parametrize(("channel", "camera"), [("green", "green_iso"), ("iso", "green_iso"), ("red", "red")])
def test_roi_selection_on_real_frame(channel, camera):
    """Smoke test on a trimmed real session: one real frame per channel and the real ROI definitions."""
    result = _suite(REAL_SESSION_HEAD, channel, camera).test_roi_selection()
    assert result.status == Status.PASSED
    assert all(np.isfinite(v) for v in result.context["metrics"].values())

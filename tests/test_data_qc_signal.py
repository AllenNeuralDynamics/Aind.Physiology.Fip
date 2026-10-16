from contraqctor.qc import ContextExportableObj, Status

from aind_physiology_fip.data_qc import FipChannelSignalTestSuite
from tests.qc_fixtures import load_stream


def _suite(root, channel="green", **kwargs):
    return FipChannelSignalTestSuite(load_stream(root, channel), **kwargs)


def test_sensor_floor_passes_on_nominal_data(session_factory):
    """A floor of 256 passes and the figure is attached as context."""
    result = _suite(session_factory()).test_sensor_floor()
    assert result.status == Status.PASSED
    assert isinstance(result.context, dict) and isinstance(result.context["asset"], ContextExportableObj)


def test_sensor_floor_fails_above_limit(session_factory):
    """A floor above the limit fails and the message names the limit."""
    result = _suite(session_factory(floor=300.0)).test_sensor_floor()
    assert result.status == Status.FAILED
    assert "exceeds the limit of 265" in result.message


def test_sensor_floor_respects_custom_limit(session_factory):
    """A lower custom limit makes the same data fail."""
    result = _suite(session_factory(floor=256.0), cmos_floor_limit=250).test_sensor_floor()
    assert result.status == Status.FAILED


def test_has_nans_passes_on_clean_data(session_factory):
    """Data without NaNs passes."""
    assert _suite(session_factory()).test_has_nans().status == Status.PASSED


def test_has_nans_counts_nans(session_factory):
    """A single injected NaN fails and is counted in the message."""
    result = _suite(session_factory(nan_at=("Fiber_1", 10))).test_has_nans()
    assert result.status == Status.FAILED
    assert "1 NaNs" in result.message


def test_sudden_changes_reports_every_fiber_and_ignores_other_columns(session_factory):
    """One result per Fiber_N column; Background and camera columns are not checked."""
    results = list(_suite(session_factory(n_fibers=3)).test_sudden_changes())
    assert len(results) == 3
    assert all(r.status == Status.PASSED for r in results)


def test_sudden_changes_flags_only_the_affected_fiber(session_factory):
    """A spike in one fiber fails only that fiber."""
    results = list(_suite(session_factory(spike_at=("Fiber_2", 50, 5000.0))).test_sudden_changes())
    status_by_fiber = {f"Fiber_{i}": r.status for i, r in enumerate(results)}
    assert status_by_fiber == {
        "Fiber_0": Status.PASSED,
        "Fiber_1": Status.PASSED,
        "Fiber_2": Status.FAILED,
        "Fiber_3": Status.PASSED,
    }


def test_sudden_changes_respects_configured_limit(session_factory):
    """A spike passes under a high limit and fails under a low one."""
    # A single-sample spike of 1500 produces two ~1500 steps (up and down)
    root = session_factory(spike_at=("Fiber_2", 50, 1500.0))
    assert all(r.status == Status.PASSED for r in _suite(root, sudden_change_limit=2000).test_sudden_changes())
    assert any(r.status == Status.FAILED for r in _suite(root, sudden_change_limit=1000).test_sudden_changes())

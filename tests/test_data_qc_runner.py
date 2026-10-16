import pytest
from contraqctor.qc import Status

from aind_physiology_fip.data_contract import dataset
from aind_physiology_fip.data_qc import DataQcCli, _run_tests, _save_assets


@pytest.fixture
def results(session_factory):
    return _run_tests(dataset(session_factory()))


def test_run_tests_produces_expected_suites(results):
    """The runner groups results by contract, dataset and per-channel suites."""
    assert {"Contract tests", "Dataset tests", "green", "iso", "red"} <= {str(k) for k in results}


def test_run_tests_has_no_errors_on_synthetic_session(results):
    """A complete synthetic session runs every suite without any ERROR result."""
    errors = [r for group in results.values() for r in group if r.status == Status.ERROR]
    assert errors == []


def test_run_tests_runs_every_qc_suite_per_channel(results):
    """Each channel runs the metadata, signal and raw-image checks."""
    names = {r.test_name for r in results["green"]}
    assert {
        "test_check_dropped_frames",
        "test_match_expected_fps",
        "test_sensor_floor",
        "test_has_nans",
        "test_sudden_changes",
        "test_frame_count",
        "test_roi_selection",
    } <= names


def test_run_tests_flags_injected_fault(session_factory):
    """A NaN injected into the signal surfaces as a failed test_has_nans."""
    results = _run_tests(dataset(session_factory(nan_at=("Fiber_0", 5))))
    failed = {r.test_name for r in results["green"] if r.status == Status.FAILED}
    assert "test_has_nans" in failed


def test_save_assets_writes_figures(results, tmp_path):
    """Figures attached to results are saved as PNGs named after their tests."""
    out = tmp_path / "assets"
    _save_assets(results, out)
    names = [p.name for p in out.glob("*.png")]
    assert any("test_sensor_floor" in n for n in names)
    assert any("test_roi_selection" in n for n in names)


def test_save_assets_is_noop_without_path(results, tmp_path, monkeypatch):
    """No asset path means nothing is written."""
    cwd = tmp_path / "cwd"
    cwd.mkdir()
    monkeypatch.chdir(cwd)
    _save_assets(results, None)
    assert list(cwd.iterdir()) == []


def test_cli_rejects_missing_data_path(tmp_path):
    """The CLI raises FileNotFoundError for a data path that does not exist."""
    with pytest.raises(FileNotFoundError):
        DataQcCli(data_path=tmp_path / "missing", asset_path=None).cli_cmd()


def test_cli_runs_end_to_end(session_factory, tmp_path):
    """The CLI runs all suites on a session and saves the figures."""
    out = tmp_path / "assets"
    DataQcCli(data_path=session_factory(), asset_path=out).cli_cmd()
    assert any(out.glob("*.png"))

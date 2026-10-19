from contraqctor.qc import Status

from aind_physiology_fip.data_contract import dataset
from aind_physiology_fip.data_qc import FipAcquisitionTestSuite


def _suite(root, **kwargs):
    ds = dataset(root)
    for name in ("green", "iso", "red"):
        ds[name].load()
    return FipAcquisitionTestSuite(ds, **kwargs)


def test_same_size_across_channels(session_factory):
    """Channels with equal row counts pass."""
    assert _suite(session_factory()).test_same_size_across_channels().status == Status.PASSED


def test_different_sizes_fail(session_factory):
    """A truncated iso channel fails and the message reports each channel's size."""
    result = _suite(session_factory(iso_rows=90)).test_same_size_across_channels()
    assert result.status == Status.FAILED
    assert "IsoCh: 90" in result.message


def test_short_recording_fails_with_default_limit(session_factory):
    """A 5 s recording is shorter than the default 15 min minimum and fails."""
    result = _suite(session_factory()).test_is_data_longer_than_minimum_duration()
    assert result.status == Status.FAILED


def test_recording_longer_than_custom_limit_passes(session_factory):
    """A recording longer than a custom minimum duration passes."""
    suite = _suite(session_factory(n_frames=100), minimum_recording_duration_s=4)  # 99 / 20 = 4.95 s
    assert suite.test_is_data_longer_than_minimum_duration().status == Status.PASSED


def test_recording_exactly_at_limit_passes(session_factory):
    """A recording whose duration equals the minimum passes (the check is strictly 'shorter than')."""
    suite = _suite(session_factory(n_frames=101, fps=20.0), minimum_recording_duration_s=5.0)
    assert suite.test_is_data_longer_than_minimum_duration().status == Status.PASSED

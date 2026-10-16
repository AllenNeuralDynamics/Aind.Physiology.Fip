import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
import pytest

from tests.qc_fixtures import SessionSpec, make_session


@pytest.fixture(autouse=True)
def _close_figures():
    yield
    plt.close("all")


@pytest.fixture
def session_factory(tmp_path):
    """Returns ``make(**spec_kwargs) -> Path`` that builds a synthetic session in a fresh directory."""
    count = 0

    def make(**spec_kwargs):
        nonlocal count
        count += 1
        return make_session(tmp_path / f"session_{count}", SessionSpec(**spec_kwargs))

    return make

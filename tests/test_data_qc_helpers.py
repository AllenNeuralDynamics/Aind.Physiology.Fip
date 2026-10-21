import matplotlib.figure
import numpy as np
import pandas as pd
import pytest

from aind_physiology_fip.data_qc_helpers import plot_sensor_floor


@pytest.mark.parametrize("wrap", [pd.Series, np.asarray])
def test_returns_figure_and_mean(wrap):
    """Returns a two-panel figure and the mean floor, for both Series and ndarray inputs."""
    data = wrap([256.0, 258.0, 260.0])
    fig, floor = plot_sensor_floor(data, "green")
    assert isinstance(fig, matplotlib.figure.Figure)
    assert floor == pytest.approx(258.0)
    assert len(fig.axes) == 2


def test_unknown_channel_falls_back_to_default_color():
    """An unrecognized channel name still plots, using the default color."""
    fig, _ = plot_sensor_floor(np.array([256.0, 257.0]), "not-a-channel")
    assert isinstance(fig, matplotlib.figure.Figure)

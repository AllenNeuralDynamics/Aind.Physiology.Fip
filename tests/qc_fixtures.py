"""Builds small synthetic FIP sessions (with optional injected faults) for the QC unit tests.

The layout and column names mirror a real acquisition (see ``tests/fixtures/real_session_head``):
every channel csv is indexed by ``ReferenceTime`` (seconds) and has ``CameraFrameNumber``,
``CameraFrameTime`` (ns), ``Background`` and ``Fiber_<n>`` columns.
"""

import dataclasses
import json
import shutil
from pathlib import Path

import numpy as np
import pandas as pd
from contraqctor import contract

from aind_physiology_fip.data_contract import dataset

EXAMPLES_DIR = Path(__file__).parents[1] / "examples"
FIXTURES_DIR = Path(__file__).parent / "fixtures"
REAL_SESSION_HEAD = FIXTURES_DIR / "real_session_head"

FRAME_SIZE = 200
CHANNEL_STRIDE = {"green": 2, "iso": 2, "red": 1}  # camera frames elapsed between consecutive rows
CHANNEL_CAMERA = {"green": "green_iso", "iso": "green_iso", "red": "red"}
ROI_INTENSITY = 2000


@dataclasses.dataclass
class SessionSpec:
    n_frames: int = 100
    fps: float = 20.0
    floor: float = 256.0
    n_fibers: int = 4
    seed: int = 0
    # Fault injection
    drop_frame_at: int | None = None  # row after which the camera frame counter skips one extra stride
    nan_at: tuple[str, int] | None = None  # (column, row)
    spike_at: tuple[str, int, float] | None = None  # (column, row, magnitude)
    clock_drift_s: float = 0.0  # extra seconds added to the camera clock on every frame
    period_jitter_s: float = 0.0  # std of the noise added to the reference clock
    raw_frames: int | None = None  # number of frames in the .bin files, defaults to n_frames
    iso_rows: int | None = None  # truncates the iso csv
    roi_noise: float = 0.01  # std of the multiplicative noise inside the ROIs
    blank_frames: bool = False  # all raw pixels are zero


def _channel_frame(rng: np.random.Generator, spec: SessionSpec, rois: dict, camera: str) -> np.ndarray:
    img = rng.normal(spec.floor, 1.0, (FRAME_SIZE, FRAME_SIZE))
    y, x = np.ogrid[:FRAME_SIZE, :FRAME_SIZE]
    for c in rois[f"camera_{camera}_roi"]:
        mask = (x - c["center"]["x"]) ** 2 + (y - c["center"]["y"]) ** 2 <= c["radius"] ** 2
        img[mask] = ROI_INTENSITY * (1 + rng.normal(0, spec.roi_noise, mask.sum()))
    if spec.blank_frames:
        img[:] = 0
    return np.clip(img, 0, 65535).astype(np.uint16)


def _channel_df(
    channel: str, spec: SessionSpec, rng: np.random.Generator, n_rows: int, *, inject_faults: bool = True
) -> pd.DataFrame:
    stride = CHANNEL_STRIDE[channel]
    i = np.arange(n_rows)
    t = 1000.0 + i / spec.fps + rng.normal(0, spec.period_jitter_s, n_rows)
    frame_number = 1000 + i * stride
    if inject_faults and spec.drop_frame_at is not None:
        frame_number = frame_number + stride * (i > spec.drop_frame_at)
    camera_time = ((t - 1000.0) + i * (spec.clock_drift_s if inject_faults else 0.0) + 500.0) * 1e9
    df = pd.DataFrame(
        {
            "ReferenceTime": t,
            "CameraFrameNumber": frame_number,
            "CameraFrameTime": camera_time.astype(np.int64),
            "Background": rng.normal(spec.floor, 1.0, n_rows),
        }
    )
    for k in range(spec.n_fibers):
        df[f"Fiber_{k}"] = rng.normal(ROI_INTENSITY, 5.0, n_rows)
    if not inject_faults:
        return df
    if spec.spike_at is not None and spec.spike_at[0] in df.columns:
        col, row, magnitude = spec.spike_at
        df.loc[row, col] += magnitude
    if spec.nan_at is not None:
        col, row = spec.nan_at
        df.loc[row, col] = np.nan
    return df


def make_session(root: Path, spec: SessionSpec | None = None) -> Path:
    """Writes a synthetic session into ``root`` and returns it."""
    spec = spec or SessionSpec()
    rng = np.random.default_rng(spec.seed)
    root.mkdir(parents=True, exist_ok=True)
    shutil.copy(REAL_SESSION_HEAD / "regions.json", root / "regions.json")
    rois = json.loads((root / "regions.json").read_text(encoding="utf-8"))
    raw_frames = spec.n_frames if spec.raw_frames is None else spec.raw_frames

    for sub in ("Logs", "SoftwareEvents"):
        shutil.copytree(REAL_SESSION_HEAD / sub, root / sub, dirs_exist_ok=True)
    shutil.copy(EXAMPLES_DIR / "AindPhysioFipRig.json", root / "Logs" / "rig_input.json")
    for name, data in (
        ("StartSessionTime", "2026-04-17T17:03:02+00:00"),
        ("EndSessionTime", "2026-04-17T17:03:07+00:00"),
        ("Annotations", "synthetic"),
        ("HardwareQA", "synthetic"),
    ):
        event = {"name": name, "timestamp": 1000.0, "timestamp_source": "harp", "data": data}
        (root / "SoftwareEvents" / f"{name}.json").write_text(json.dumps(event) + "\n", encoding="utf-8")

    for camera, stride in (("green_iso", 2), ("red", 1)):
        meta = _channel_df("green" if stride == 2 else "red", spec, rng, spec.n_frames, inject_faults=False)
        meta = meta[["ReferenceTime", "CameraFrameNumber", "CameraFrameTime"]].copy()
        meta["CpuTime"] = pd.date_range("2026-04-17T17:03:02Z", periods=spec.n_frames, freq="50ms").strftime(
            "%Y-%m-%dT%H:%M:%S.%fZ"
        )
        meta.to_csv(root / f"camera_{camera}_metadata.csv", index=False)

    for channel in CHANNEL_STRIDE:
        _channel_df(channel, spec, rng, 10, inject_faults=False).to_csv(root / f"background_{channel}.csv", index=False)
        n_rows = spec.iso_rows if (channel == "iso" and spec.iso_rows is not None) else spec.n_frames
        _channel_df(channel, spec, rng, n_rows).to_csv(root / f"{channel}.csv", index=False)
        shutil.copy(REAL_SESSION_HEAD / f"{channel}_metadata.json", root / f"{channel}_metadata.json")
        frame = _channel_frame(rng, spec, rois, CHANNEL_CAMERA[channel])
        # the reader interprets the file as column major
        (root / f"{channel}.bin").write_bytes(frame.T.tobytes() * raw_frames)
        (root / f"background_{channel}.bin").write_bytes(frame.T.tobytes() * 10)
    return root


def load_stream(root: Path, name: str) -> contract.DataStream:
    """Returns the loaded data stream called ``name`` of the session at ``root``."""
    stream = dataset(root)[name]
    stream.load()
    return stream

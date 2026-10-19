"""Trims a real FIP session into the small fixture used by the QC unit tests.

Source asset: s3://aind-open-data/816214_2026-04-17_17-03-02/fib/fip_2026-04-17T100358/
Download the small files plus the first ~30 KB of each csv and the first frame of each .bin, then run:

    uv run python scripts/make_qc_fixture.py --src <download_dir> --dst tests/fixtures/real_session_head
"""

import argparse
import json
import shutil
from pathlib import Path

FRAME_BYTES = 200 * 200 * 2  # 200x200 uint16
N_ROWS = 120
N_FRAMES = 1
CSVS = ["green", "iso", "red", "camera_green_iso_metadata", "camera_red_metadata"]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", type=Path, required=True)
    parser.add_argument("--dst", type=Path, required=True)
    args = parser.parse_args()
    args.dst.mkdir(parents=True, exist_ok=True)

    for name in (
        "regions.json",
        "green_metadata.json",
        "iso_metadata.json",
        "red_metadata.json",
        "Logs/session_input.json",
        "SoftwareEvents/Regions.json",
    ):
        (args.dst / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(args.src / name, args.dst / name)
    # anonymize the experimenter(s)
    session_path = args.dst / "Logs/session_input.json"
    session = json.loads(session_path.read_text(encoding="utf-8"))
    session["experimenter"] = ["anonymous.experimenter"]
    session_path.write_text(json.dumps(session, indent=2), encoding="utf-8")
    for name in CSVS:
        # newline="" preserves the original line endings; the last (possibly cut) line is dropped by the row limit
        with open(args.src / f"{name}.csv", newline="", encoding="utf-8") as f:
            lines = f.readlines()[: N_ROWS + 1]
        with open(args.dst / f"{name}.csv", "w", newline="", encoding="utf-8") as f:
            f.writelines(lines)
    for color in ("green", "iso", "red"):
        data = (args.src / f"{color}.bin").read_bytes()[: N_FRAMES * FRAME_BYTES]
        (args.dst / f"{color}.bin").write_bytes(data)


if __name__ == "__main__":
    main()

"""Add session start and end times to session_input.json from camera metadata."""

import json
import sys
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

import pandas as pd


def add_session_timing(data_dir: str, local_timezone: str = "America/Los_Angeles") -> None:
    """
    Add session_start_time and session_end_time to session_input.json.

    Reads camera metadata CSV to extract first and last CpuTime values,
    converts them to local timezone, and adds them to session_input.json.

    Parameters
    ----------
    data_dir : str
        Path to the data directory containing camera metadata and Logs folder
    local_timezone : str
        IANA timezone string (default: "America/Los_Angeles")
    """

    data_path = Path(data_dir)
    session_json_path = data_path / "Logs" / "session_input.json"

    if not session_json_path.exists():
        raise FileNotFoundError(f"Session JSON not found at {session_json_path}")

    # Extract timing from camera metadata
    local_tz = ZoneInfo(local_timezone)
    start_time = None
    end_time = None

    # Try camera_green_iso_metadata first, fall back to camera_red_metadata
    for metadata_file in ["camera_green_iso_metadata.csv", "camera_red_metadata.csv"]:
        metadata_path = data_path / metadata_file
        if metadata_path.exists():
            metadata = pd.read_csv(metadata_path)
            if "CpuTime" in metadata.columns and not metadata.empty:
                # CpuTime is in timezone-aware ISO 8601 format (UTC)
                start_utc = datetime.fromisoformat(metadata["CpuTime"].iloc[0])
                end_utc = datetime.fromisoformat(metadata["CpuTime"].iloc[-1])
                start_time = start_utc.astimezone(local_tz)
                end_time = end_utc.astimezone(local_tz)
                break

    if start_time is None:
        raise ValueError(
            "Could not extract session timing from camera metadata. "
            "Expected to find CpuTime column in camera_green_iso_metadata.csv or camera_red_metadata.csv."
        )

    # Read existing session JSON
    with open(session_json_path, "r") as f:
        session_data = json.load(f)

    # Add timing fields
    session_data["session_start_time"] = start_time.isoformat()
    session_data["session_end_time"] = end_time.isoformat()

    # Write back to file
    with open(session_json_path, "w") as f:
        json.dump(session_data, f, indent=2)

    print(f"Added session timing to {session_json_path}")
    print(f"  Start: {start_time.isoformat()}")
    print(f"  End: {end_time.isoformat()}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python add_session_timing.py <data_directory> [timezone]")
        print("Example: python add_session_timing.py /path/to/data America/Los_Angeles")
        sys.exit(1)

    data_directory = sys.argv[1]
    timezone = sys.argv[2] if len(sys.argv) > 2 else "America/Los_Angeles"
    
    try:
        add_session_timing(data_directory, timezone)
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)


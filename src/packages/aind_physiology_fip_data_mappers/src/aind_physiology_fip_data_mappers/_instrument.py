"""AIND instrument mapper extension point."""

from pathlib import Path
from typing import Any

from ._base import DataMapper


class AindInstrumentDataMapper(DataMapper[Any]):
    """Map a FIP rig to an AIND instrument record."""

    def __init__(self, data_path: Path):
        self._data_path = Path(data_path)
        self._mapped = None

    def map(self) -> Any:
        raise NotImplementedError("AIND instrument mapping is pending implementation")

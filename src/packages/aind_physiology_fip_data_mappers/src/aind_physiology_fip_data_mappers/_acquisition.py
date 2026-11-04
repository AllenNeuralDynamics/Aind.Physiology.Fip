"""AIND acquisition mapper extension point."""

from datetime import datetime
from pathlib import Path
from typing import Any

from ._base import DataMapper


class AindAcquisitionDataMapper(DataMapper[Any]):
    """Placeholder for mapping a FIP session to an AIND acquisition record."""

    def __init__(
        self,
        data_path: Path,
        repository_path: Path = Path("."),
        session_end_time: datetime | None = None,
    ):
        self._data_path = Path(data_path)
        self._repository_path = Path(repository_path)
        self._session_end_time = session_end_time
        self._mapped = None

    def map(self) -> Any:
        raise NotImplementedError("AIND acquisition mapping is pending implementation")

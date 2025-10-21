import importlib.util

if importlib.util.find_spec("aind_data_schema") is None:
    raise ImportError(
        "The 'aind-data-schema' package is required to use this module. "
        "Install the optional dependencies defined in `project.toml` "
    )
import logging
from typing import Optional

from aind_data_schema.core.instrument import Instrument

from ._base import AindDataSchemaMapper

logger = logging.getLogger(__name__)


class InstrumentMapper(AindDataSchemaMapper[Instrument]):
    """
    Maps raw instrument data to the AIND data schema Instrument format.
    """

    def __init__(self, *args, **kwargs):
        self._mapped: Optional[Instrument] = None

    def map(self) -> Instrument:
        """
        Maps the raw instrument data to the AIND data schema Instrument format.
        Returns:
            Mapped Instrument object
        """
        raise NotImplementedError("Instrument mapping not yet implemented.")

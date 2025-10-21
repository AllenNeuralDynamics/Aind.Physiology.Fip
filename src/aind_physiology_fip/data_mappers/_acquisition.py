import importlib.util

if importlib.util.find_spec("aind_data_schema") is None:
    raise ImportError(
        "The 'aind-data-schema' package is required to use this module. "
        "Install the optional dependencies defined in `project.toml` "
    )
import logging
from typing import Optional

from aind_data_schema.core.acquisition import Acquisition

from ._base import AindDataSchemaMapper

logger = logging.getLogger(__name__)


class AcquisitionMapper(AindDataSchemaMapper[Acquisition]):
    """
    Maps raw acquisition data to the AIND data schema Acquisition format.
    """

    def __init__(self, *args, **kwargs):
        self._mapped: Optional[Acquisition] = None

    def map(self) -> Acquisition:
        """
        Maps the raw acquisition data to the AIND data schema Acquisition format.

        Returns:
            Mapped Acquisition object
        """
        raise NotImplementedError("Acquisition mapping not yet implemented.")

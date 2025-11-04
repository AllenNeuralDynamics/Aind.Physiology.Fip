import logging
import os
from pathlib import Path

import pydantic
import pydantic_settings

from ._acquisition import ProtoAcquisitionDataSchema as ProtoAcquisitionDataSchema
from ._acquisition import ProtoAcquisitionMapper

logger = logging.getLogger(__name__)


class DataMapperCli(pydantic_settings.BaseSettings, cli_kebab_case=True):
    """CLI for generating AIND metadata from raw FIP data."""

    data_path: os.PathLike = pydantic.Field(description="Path to the session data directory.")

    def cli_cmd(self):
        logger.info("Mapping metadata directly from dataset.")
        acquisition_mapped = ProtoAcquisitionMapper(self.data_path).map()
        logger.info("Writing fip.json to %s", self.data_path)
        # According to @dbirman, the name of this file MUST match the extractor, so we hardcode it here.
        with open(Path(self.data_path) / "fip.json", "w", encoding="utf-8") as f:
            f.write(acquisition_mapped.model_dump_json(indent=2))
        logger.info("Mapping completed!")


if __name__ == "__main__":
    pydantic_settings.CliApp().run(DataMapperCli)

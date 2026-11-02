import logging
import os
from pathlib import Path

import pydantic
import pydantic_settings

from ._acquisition import REPOSITORY_PATH
from ._acquisition import AindAcquisitionDataMapper as AindAcquisitionDataMapper
from ._instrument import AindInstrumentDataMapper as AindInstrumentDataMapper

logger = logging.getLogger(__name__)

INSTRUMENT_FILE_NAME = "instrument_fib.json"
ACQUISITION_FILE_NAME = "acquisition_fib.json"


class DataMapperCli(pydantic_settings.BaseSettings, cli_kebab_case=True):
    """CLI for generating AIND metadata from raw FIP data."""

    data_path: os.PathLike = pydantic.Field(description="Path to the session data directory.")
    repository_path: os.PathLike = pydantic.Field(
        default=REPOSITORY_PATH,
        description=(
            "Checkout of the Aind.Physiology.Fip repository the session was acquired with, whose remote, "
            "commit and Bonsai environment are recorded as the acquisition code. Defaults to the checkout "
            "this package runs from."
        ),
    )
    clock_generator_name: str | None = pydantic.Field(
        default=None,
        description=(
            "Name of the Harp clock generator (WhiteRabbit), when FIP is acquired alongside a behavior "
            "task. It must match the name that task uses for the same board. Leave unset for a FIP-only "
            "acquisition, which records no clock generator. Settable as CLOCK_GENERATOR_NAME in the "
            "environment."
        ),
    )
    objective_serial_number: str | None = pydantic.Field(
        default=None,
        description=(
            "Serial number of the objective, which differs between instruments. Settable as "
            "OBJECTIVE_SERIAL_NUMBER in the environment so each acquisition computer can be "
            "configured once, rather than passed on every run."
        ),
    )

    def cli_cmd(self):
        logger.info("Mapping metadata directly from dataset.")
        instrument_mapped = AindInstrumentDataMapper(
            self.data_path, self.clock_generator_name, self.objective_serial_number
        ).map()
        logger.info("Writing %s to %s", INSTRUMENT_FILE_NAME, self.data_path)
        with open(Path(self.data_path) / INSTRUMENT_FILE_NAME, "w", encoding="utf-8") as f:
            f.write(instrument_mapped.model_dump_json(indent=2))

        acquisition_mapped = AindAcquisitionDataMapper(self.data_path, self.repository_path).map()
        logger.info("Writing %s to %s", ACQUISITION_FILE_NAME, self.data_path)
        with open(Path(self.data_path) / ACQUISITION_FILE_NAME, "w", encoding="utf-8") as f:
            f.write(acquisition_mapped.model_dump_json(indent=2))
        logger.info("Mapping completed!")


if __name__ == "__main__":
    pydantic_settings.CliApp().run(DataMapperCli)

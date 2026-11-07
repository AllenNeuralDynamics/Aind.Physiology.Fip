import logging
import os
from datetime import datetime
from pathlib import Path

import pydantic
import pydantic_settings

from ._acquisition import AindAcquisitionDataMapper as AindAcquisitionDataMapper
from ._instrument import AindInstrumentDataMapper as AindInstrumentDataMapper
from ._utils import TrackedDeviceName

logger = logging.getLogger(__name__)

INSTRUMENT_FILE_NAME = "instrument_fib.json"


class DataMapperCli(pydantic_settings.BaseSettings, cli_kebab_case=True):
    """CLI for generating AIND metadata from raw FIP data."""

    data_path: os.PathLike = pydantic.Field(description="Path to the session data directory.")
    repository_path: Path = pydantic.Field(default=Path("."), description="Path to the repository.")
    session_end_time: datetime | None = pydantic.Field(default=None, description="Session end time in ISO format.")
    suffix: str | None = pydantic.Field(default="fip", description="Suffix for output filenames.")
    clock_generator_name: str = pydantic.Field(
        default=TrackedDeviceName.CLOCK_GENERATOR,
        description=(
            "Name of the Harp clock generator (WhiteRabbit) of the behavior task FIP is acquired "
            "alongside. It must match the name that task uses for the same board, so the two "
            "instruments merge into one. Defaults to the name the behavior tasks use; override it "
            "only for a task that names it differently. Settable as CLOCK_GENERATOR_NAME in the "
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
        instrument_mapped = AindInstrumentDataMapper(
            data_path=Path(self.data_path),
            clock_generator_name=self.clock_generator_name,
            objective_serial_number=self.objective_serial_number,
        ).map()
        logger.info("Writing %s to %s", INSTRUMENT_FILE_NAME, self.data_path)
        with open(Path(self.data_path) / INSTRUMENT_FILE_NAME, "w", encoding="utf-8") as f:
            f.write(instrument_mapped.model_dump_json(indent=2))

        AindAcquisitionDataMapper(
            data_path=Path(self.data_path),
            repository_path=self.repository_path,
            session_end_time=self.session_end_time,
        ).map()


def main() -> None:
    pydantic_settings.CliApp().run(DataMapperCli)


if __name__ == "__main__":
    main()

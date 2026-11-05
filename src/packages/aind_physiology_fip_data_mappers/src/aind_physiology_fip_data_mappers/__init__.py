import os
from datetime import datetime
from pathlib import Path

import pydantic
import pydantic_settings

from ._acquisition import AindAcquisitionDataMapper as AindAcquisitionDataMapper
from ._instrument import AindInstrumentDataMapper as AindInstrumentDataMapper
from ._proto_acquisition import ProtoAcquisitionDataSchema as ProtoAcquisitionDataSchema
from ._proto_acquisition import ProtoAcquisitionMapper as ProtoAcquisitionMapper


class DataMapperCli(pydantic_settings.BaseSettings, cli_kebab_case=True):
    """CLI for generating AIND metadata from raw FIP data."""

    data_path: os.PathLike = pydantic.Field(description="Path to the session data directory.")
    repository_path: Path = pydantic.Field(default=Path("."), description="Path to the repository.")
    session_end_time: datetime | None = pydantic.Field(default=None, description="Session end time in ISO format.")
    suffix: str | None = pydantic.Field(default="fip", description="Suffix for output filenames.")

    def cli_cmd(self):
        AindAcquisitionDataMapper(
            data_path=Path(self.data_path),
            repository_path=self.repository_path,
            session_end_time=self.session_end_time,
        ).map()
        AindInstrumentDataMapper(data_path=Path(self.data_path)).map()


def main() -> None:
    pydantic_settings.CliApp().run(DataMapperCli)


if __name__ == "__main__":
    main()

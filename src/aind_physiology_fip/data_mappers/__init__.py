import logging
import os
from pathlib import Path

import pydantic
import pydantic_settings
from aind_behavior_services import AindBehaviorSessionModel
from aind_behavior_services.utils import model_from_json_file
from git import Repo

from aind_physiology_fip.rig import AindPhysioFipRig

from ._acquisition import AcquisitionMapper
from ._instrument import InstrumentMapper

logger = logging.getLogger(__name__)


class DataMapperCli(pydantic_settings.BaseSettings, cli_kebab_case=True):
    data_path: os.PathLike = pydantic.Field(description="Path to the session data directory.")
    db_root: os.PathLike = pydantic.Field(
        default=Path(r"\\allen\aind\scratch\AindBehavior.db\AindVrForaging"),
        description="Root directory for the database for additional metadata.",
    )
    repo_path: os.PathLike = pydantic.Field(
        default=Path("."), description="Path to the repository. By default it will use the current directory."
    )
    # TODO We may need more information here

    def cli_cmd(self):
        logger.info("Mapping metadata directly from dataset.")
        # TODO
        for candidate_epoch in Path(self.data_path).iterdir():
            if not candidate_epoch.is_dir():
                continue
            if (candidate_epoch / "Logs" / "session_input.json").exists():
                logger.debug("Using session_input.json from %s", candidate_epoch)
                session = model_from_json_file(
                    candidate_epoch / "Logs" / "session_input.json", AindBehaviorSessionModel
                )
                rig = model_from_json_file(candidate_epoch / "Logs" / "rig_input.json", AindPhysioFipRig)
                break
        else:
            raise FileNotFoundError(f"Could not find session_input.json in any epoch under {self.data_path}")

        repo = Repo(self.repo_path)
        # TODO Add we may need more information
        acquisition_mapped = AcquisitionMapper(session, rig, repo=repo).map()
        instrument_mapped = InstrumentMapper(session, rig).map()

        acquisition_mapped.instrument_id = instrument_mapped.instrument_id
        logger.info("Writing acquisition.json to %s", self.data_path)
        acquisition_mapped.write_standard_file(Path(self.data_path), suffix="fip")
        logger.info("Writing instrument.json to %s", self.data_path)
        instrument_mapped.write_standard_file(Path(self.data_path), suffix="fip")
        logger.info("Mapping completed!")


if __name__ == "__main__":
    pydantic_settings.CliApp().run(DataMapperCli)

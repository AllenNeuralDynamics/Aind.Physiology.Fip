import importlib.util
import os
from datetime import datetime

if importlib.util.find_spec("aind_data_schema") is None:
    raise ImportError(
        "The 'aind-data-schema' package is required to use this module. "
        "Install the optional dependencies defined in `project.toml` "
    )
import logging
from pathlib import Path
from typing import Optional, cast

import pydantic
from aind_behavior_services.session import AindBehaviorSessionModel
from pandas import DataFrame

from aind_physiology_fip.data_contract import dataset
from aind_physiology_fip.data_mappers._base import AindDataSchemaMapper
from aind_physiology_fip.rig import AindPhysioFipRig

logger = logging.getLogger(__name__)


class ProtoAcquisitionDataSchema(pydantic.BaseModel):
    """
    Prototype Acquisition model for raw acquisition data.
    This will get used by the upstream aind-data-schema metadata mapper to
    create a final Acquisition object.
    """

    data_stream_metadata: list["_FipDataStreamMetadata"] = pydantic.Field(
        min_length=1, description="Metadata for each data stream in the acquisition."
    )
    session: AindBehaviorSessionModel = pydantic.Field(
        description="The session information that instantiated the acquisition."
    )
    rig: AindPhysioFipRig = pydantic.Field(description="The rig configuration that instantiated the acquisition.")


class _FipDataStreamMetadata(pydantic.BaseModel):
    id: str
    start_time: pydantic.AwareDatetime
    end_time: pydantic.AwareDatetime


class ProtoAcquisitionMapper(AindDataSchemaMapper[ProtoAcquisitionDataSchema]):
    """
    Maps raw acquisition data to the AIND data schema Acquisition format.
    """

    def __init__(self, data_directory: os.PathLike):
        self._mapped = None
        self._data_directory = data_directory

    def map(self) -> ProtoAcquisitionDataSchema:
        epochs = list((Path(self._data_directory) / "fib").glob("fip_*"))
        session, rig = self._extract_session_and_rig(epochs)
        data_streams_metadata = self._extract_start_end_times(epochs)

        return ProtoAcquisitionDataSchema(data_stream_metadata=data_streams_metadata, session=session, rig=rig)

    @staticmethod
    def _extract_start_end_times(epochs: list[Path]) -> list[_FipDataStreamMetadata]:
        data_streams = []
        _candidate_streams = ["camera_green_iso_metadata", "camera_red_metadata"]
        for epoch in epochs:
            if not epoch.is_dir():
                continue
            try:
                this_epoch = dataset(root=epoch)
                for stream in _candidate_streams:
                    logger.debug(f"Checking for timing in stream: {stream}")
                    start_utc, end_utc = ProtoAcquisitionMapper._extract_from_df(
                        cast(DataFrame, this_epoch[stream].read())
                    )
                    data_streams.append(_FipDataStreamMetadata(id=epoch.name, start_time=start_utc, end_time=end_utc))
            except Exception as e:
                logger.warning(f"Failed to load FIP dataset at {epoch}: {e}")
                continue
        return data_streams

    @staticmethod
    def _extract_from_df(df: DataFrame) -> tuple[datetime, datetime]:
        if df is None or df.empty:
            raise ValueError("DataFrame is None or empty.")
        start_utc = datetime.fromisoformat(df["CpuTime"].iloc[0])
        end_utc = datetime.fromisoformat(df["CpuTime"].iloc[-1])
        return start_utc, end_utc

    @staticmethod
    def _extract_session_and_rig(epochs: list[Path]) -> tuple[AindBehaviorSessionModel, AindPhysioFipRig]:
        session: Optional[AindBehaviorSessionModel] = None
        rig: Optional[AindPhysioFipRig] = None
        for epoch in epochs:
            if not epoch.is_dir():
                continue
            _dataset = dataset(root=epoch)
            if session is None:
                try:
                    session = _dataset["session_input"].read()
                except Exception as e:
                    logger.debug(f"No session_input found in dataset at {epoch}: {e}")
            if rig is None:
                try:
                    rig = _dataset["rig_input"].read()
                except Exception as e:
                    logger.debug(f"No rig_input found in dataset at {epoch}: {e}")
                    continue
        if session is None:
            raise ValueError("No session_input found in any of the provided epochs.")
        if rig is None:
            raise ValueError("No rig_input found in any of the provided epochs.")
        return session, rig


if __name__ == "__main__":
    import logging

    logging.basicConfig(level=logging.DEBUG)
    path = r"\\allen\aind\scratch\bruno.cruz\fip_tests\781896_2025-07-18T190319Z"
    mapper = ProtoAcquisitionMapper(data_directory=path)
    acquisition = mapper.map()
    print(acquisition.model_dump_json(indent=4))

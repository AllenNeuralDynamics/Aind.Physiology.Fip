"""
Acquisition Data Mapper Module

This module provides functionality for mapping raw fiber photometry (FIP)
acquisition data to a prototype acquisition data schema compatible with
the AIND (Allen Institute for Neural Dynamics) data schema format.

The module processes raw FIP data directories containing multiple
acquisition epochs and extracts timing information, session metadata,
and rig configuration to create a standardized acquisition data model.

The module requires the 'aind-data-schema' package to be installed. This
is typically included as an optional dependency in the project's
pyproject.toml file.

Classes:
    ProtoAcquisitionDataSchema: Pydantic model representing the prototype
                                acquisition data structure.
    ProtoAcquisitionMapper: Mapper class that transforms raw acquisition
                           data into the prototype schema format.
    _FipDataStreamMetadata: Internal model for individual data stream
                           timing metadata.

Example:
    >>> from pathlib import Path
    >>> mapper = ProtoAcquisitionMapper(data_directory=Path("data"))
    >>> acquisition = mapper.map()
    >>> print(acquisition.model_dump_json(indent=4))
"""

import importlib.util
import os
from datetime import datetime

# Check if the required aind-data-schema package is available
# This package contains the core data schemas used by AIND
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

# Initialize module-level logger for debugging and status messages
logger = logging.getLogger(__name__)


class ProtoAcquisitionDataSchema(pydantic.BaseModel):
    """
    Prototype acquisition model for raw acquisition data.

    This model represents the intermediate data structure for acquisition
    metadata before final transformation to the AIND data schema
    Acquisition format. It aggregates timing information from multiple
    data streams along with session and rig configuration metadata.

    This prototype will be consumed by upstream aind-metadata-extractor or
    aind-data-schema metadata mappers to create the final standardized Acquisition object.

    Attributes:
        data_stream_metadata (list[_FipDataStreamMetadata]): List of
            metadata objects for each data stream in the acquisition,
            including timing information. Must contain at least one entry.
        session (AindBehaviorSessionModel): The behavior session metadata
            that instantiated this acquisition.
        rig (AindPhysioFipRig): The FIP rig configuration metadata that
            was used for this acquisition.
    """

    data_stream_metadata: list["_FipDataStreamMetadata"] = pydantic.Field(
        min_length=1,
        description="Metadata for each data stream in the acquisition.",
    )
    session: AindBehaviorSessionModel = pydantic.Field(
        description="The session information that instantiated the acquisition."
    )
    rig: AindPhysioFipRig = pydantic.Field(description="The rig configuration that instantiated the acquisition.")


class _FipDataStreamMetadata(pydantic.BaseModel):
    """
    Internal metadata model for individual FIP data streams.

    Captures timing information for a single data stream within a FIP
    acquisition epoch, including the stream identifier and temporal
    boundaries.

    Attributes:
        id (str): Unique identifier for the data stream (typically the
            epoch name).
        start_time (pydantic.AwareDatetime): Timezone-aware timestamp
            marking the beginning of data collection for this stream.
        end_time (pydantic.AwareDatetime): Timezone-aware timestamp
            marking the end of data collection for this stream.
    """

    id: str
    start_time: pydantic.AwareDatetime
    end_time: pydantic.AwareDatetime


class ProtoAcquisitionMapper(AindDataSchemaMapper[ProtoAcquisitionDataSchema]):
    """
    Maps raw acquisition data to the prototype acquisition schema format.

    This mapper processes raw FIP acquisition data from a directory
    structure containing multiple acquisition epochs. It extracts timing
    information from data streams, session metadata, and rig configuration
    to create a complete ProtoAcquisitionDataSchema object.

    The mapper scans for FIP epoch directories (named 'fip_*') within a
    'fib' subdirectory and aggregates metadata from all discovered epochs.

    Attributes:
        _mapped (Optional[ProtoAcquisitionDataSchema]): The cached mapped
            acquisition object, populated after map() is called.
        _data_directory (os.PathLike): Path to the root data directory
            containing the acquisition data.

    Example:
        >>> from pathlib import Path
        >>> mapper = ProtoAcquisitionMapper(Path("./data"))
        >>> acquisition = mapper.map()
        >>> print(acquisition.session.subject_id)
    """

    def __init__(self, data_directory: os.PathLike):
        """
        Initialize the ProtoAcquisitionMapper.

        Args:
            data_directory (os.PathLike): Path to the root directory
                containing the FIP acquisition data. Should contain a
                'fib' subdirectory with FIP epoch folders.
        """
        # Initialize the cached mapping result to None
        self._mapped = None
        # Store the data directory path for access during mapping
        self._data_directory = data_directory

    def map(self) -> ProtoAcquisitionDataSchema:
        """
        Map raw acquisition data to prototype acquisition schema format.

        This method orchestrates the extraction of all acquisition metadata
        including data stream timing, session information, and rig
        configuration from the raw data directory structure.

        Returns:
            ProtoAcquisitionDataSchema: Complete prototype acquisition
                object containing all extracted metadata.

        Raises:
            ValueError: If no valid session or rig configuration is found
                in any of the processed epochs.
        """
        # Discover all FIP epoch directories within the data directory
        epochs = list((Path(self._data_directory) / "fib").glob("fip_*"))

        # Extract session and rig metadata from the epochs
        session, rig = self._extract_session_and_rig(epochs)

        # Extract timing information from all data streams
        data_streams_metadata = self._extract_start_end_times(epochs)

        return ProtoAcquisitionDataSchema(
            data_stream_metadata=data_streams_metadata,
            session=session,
            rig=rig,
        )

    @staticmethod
    def _extract_start_end_times(
        epochs: list[Path],
    ) -> list[_FipDataStreamMetadata]:
        """
        Extract timing metadata from all FIP acquisition epochs.

        Processes each epoch directory to extract start and end timestamps
        from candidate data streams. This method attempts to read timing
        information from camera metadata streams and creates a metadata
        object for each successfully processed stream.

        Args:
            epochs (list[Path]): List of Path objects pointing to FIP
                epoch directories to process.

        Returns:
            list[_FipDataStreamMetadata]: List of metadata objects
                containing timing information for each processed data
                stream.

        Note:
            Failed epochs are logged as warnings and skipped, allowing
            partial processing of multi-epoch acquisitions.
        """
        data_streams = []
        # List of camera metadata streams to check for timing information
        _candidate_streams = [
            "camera_green_iso_metadata",
            "camera_red_metadata",
        ]
        for epoch in epochs:
            # Skip non-directory entries
            if not epoch.is_dir():
                continue
            try:
                # Load the dataset for this epoch
                this_epoch = dataset(root=epoch)
                for stream in _candidate_streams:
                    logger.debug(f"Checking for timing in stream: {stream}")
                    # Extract start/end times from the stream DataFrame
                    start_utc, end_utc = ProtoAcquisitionMapper._extract_from_df(
                        cast(DataFrame, this_epoch[stream].read())
                    )
                    # Create metadata object for this data stream
                    data_streams.append(
                        _FipDataStreamMetadata(
                            id=epoch.name,
                            start_time=start_utc,
                            end_time=end_utc,
                        )
                    )
            except Exception as e:
                # Log warning but continue processing other epochs
                logger.warning(f"Failed to load FIP dataset at {epoch}: {e}")
                continue
        return data_streams

    @staticmethod
    def _extract_from_df(df: DataFrame) -> tuple[datetime, datetime]:
        """
        Extract start and end timestamps from a DataFrame.

        Retrieves the first and last CpuTime entries from a metadata
        DataFrame to determine the temporal boundaries of data collection.

        Args:
            df (DataFrame): Pandas DataFrame containing a 'CpuTime' column
                with ISO format datetime strings.

        Returns:
            tuple[datetime, datetime]: A tuple containing the start time
                (first entry) and end time (last entry).

        Raises:
            ValueError: If the DataFrame is None or empty.
        """
        if df is None or df.empty:
            raise ValueError("DataFrame is None or empty.")
        # Parse ISO format timestamps from first and last rows
        start_utc = datetime.fromisoformat(df["CpuTime"].iloc[0])
        end_utc = datetime.fromisoformat(df["CpuTime"].iloc[-1])
        return start_utc, end_utc

    @staticmethod
    def _extract_session_and_rig(
        epochs: list[Path],
    ) -> tuple[AindBehaviorSessionModel, AindPhysioFipRig]:
        """
        Extract session and rig configuration from acquisition epochs.

        Searches through the provided epochs to find valid session and rig
        configuration data. Returns the first valid session and rig found
        across all epochs.

        Args:
            epochs (list[Path]): List of Path objects pointing to FIP
                epoch directories to search.

        Returns:
            tuple[AindBehaviorSessionModel, AindPhysioFipRig]: A tuple
                containing the session model and rig configuration.

        Raises:
            ValueError: If no valid session_input or rig_input is found
                in any of the provided epochs.
        """
        session: Optional[AindBehaviorSessionModel] = None
        rig: Optional[AindPhysioFipRig] = None
        for epoch in epochs:
            # Skip non-directory entries
            if not epoch.is_dir():
                continue
            # Load the dataset for this epoch
            _dataset = dataset(root=epoch)

            # Try to extract session metadata if not already found
            if session is None:
                try:
                    session = _dataset["session_input"].read()
                except Exception as e:
                    logger.debug(f"No session_input found in dataset at {epoch}: {e}")

            # Try to extract rig configuration if not already found
            if rig is None:
                try:
                    rig = _dataset["rig_input"].read()
                except Exception as e:
                    logger.debug(f"No rig_input found in dataset at {epoch}: {e}")
                    continue

        # Validate that required metadata was found
        if session is None:
            raise ValueError("No session_input found in any of the provided epochs.")
        if rig is None:
            raise ValueError("No rig_input found in any of the provided epochs.")
        return session, rig

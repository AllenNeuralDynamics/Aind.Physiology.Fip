"""What each of a subject's fibers was implanted to measure, from aind-metadata-service.

The values are not on the rig, so the acquisition mapper fetches them from the service's
``intended_measurements`` endpoint, as the aind-metadata-mapper FIP mapper did at upload.
The lookup fails loudly: a session whose measurements cannot be fetched stays on the rig
until it is mapped again, by which point the service is back or the response has been
saved to a file and passed in instead.

TODO: Temporary. Replace with the powerlims endpoint once procedures, and with them the
intended measurements, are in Dataverse
(https://github.com/AllenNeuralDynamics/Aind.Physiology.Fip/issues/101).

Functions:
    fetch_intended_measurements: Query the service for a subject's intended measurements.
    parse_intended_measurements: Read the service's response into ``IntendedMeasurements``.
"""

import json
import logging
import time
import urllib.error
import urllib.request
from typing import Any

logger = logging.getLogger(__name__)

METADATA_SERVICE_URL = "http://aind-metadata-service"

# Fiber name ("Fiber_0") to the measurement of each channel, keyed by ``ChannelSpec.measurement_key``.
IntendedMeasurements = dict[str, dict[str, str | None]]


def fetch_intended_measurements(
    subject_id: str, metadata_service_url: str = METADATA_SERVICE_URL
) -> IntendedMeasurements:
    """Query aind-metadata-service for the intended measurements of a subject's fibers.

    Raises:
        urllib.error.URLError: If the service cannot be reached after a few attempts.
        ValueError: If the service has no measurements for the subject, answers with an
            error, or returns a response that cannot be read.
    """
    url = f"{metadata_service_url.rstrip('/')}/intended_measurements/{subject_id}"
    status, body = _get(url)
    # The service answers 300 when the subject has more than one fiber.
    if status not in (200, 300):
        raise ValueError(f"No intended measurements for subject {subject_id}: {url} answered {status}: {body[:500]!r}")
    return parse_intended_measurements(json.loads(body))


def _get(url: str, timeout: float = 10, attempts: int = 3, retry_delay: float = 1) -> tuple[int, bytes]:
    """The status and body of a GET request, retried while the service cannot be reached."""
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(url, timeout=timeout) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            # Unlike requests, urllib raises for every status outside 2xx, the service's 300 included.
            return error.code, error.read()
        except urllib.error.URLError as error:
            if attempt == attempts:
                raise
            logger.warning(f"Could not reach {url} (attempt {attempt} of {attempts}): {error}")
            time.sleep(retry_delay)


def parse_intended_measurements(response: dict[str, Any]) -> IntendedMeasurements:
    """Read an ``intended_measurements`` response into the measurements of each fiber.

    ``data`` holds one row per fiber, or a single row on its own. FIP has no blue channel, so
    the blue measurement is not read.

    Raises:
        ValueError: If the response holds no fiber.
    """
    rows = response.get("data") or []
    if isinstance(rows, dict):
        rows = [rows]

    result = {
        row["fiber_name"]: {
            "G": _measurement(row.get("intended_measurement_G")),
            "Iso": _measurement(row.get("intended_measurement_Iso")),
            "R": _measurement(row.get("intended_measurement_R")),
        }
        for row in rows
        if row.get("fiber_name")
    }
    if not result:
        raise ValueError("The response holds no fiber.")
    return result


def _measurement(value: Any) -> str | None:
    """A channel's measurement, or None where the service has the string "None" or nothing."""
    return None if value in (None, "", "None") else str(value)

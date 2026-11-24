"""Tests fetching and reading intended measurements from aind-metadata-service, with the service mocked."""

import io
import json
import urllib.error
from unittest.mock import MagicMock, patch

import pytest
from aind_physiology_fip_data_mappers._intended_measurements import (
    METADATA_SERVICE_URL,
    fetch_intended_measurements,
    parse_intended_measurements,
)

# aind-metadata-service's response for a real subject with four fibers, which it answers with a 300.
RESPONSE = {
    "message": "Multiple Items Found. All Models Valid.",
    "data": [
        {
            "fiber_name": "Fiber_0",
            "intended_measurement_R": "acetylcholine",
            "intended_measurement_G": "serotonin",
            "intended_measurement_B": "None",
            "intended_measurement_Iso": "control",
        },
        {
            "fiber_name": "Fiber_1",
            "intended_measurement_R": "None",
            "intended_measurement_G": "calcium",
            "intended_measurement_B": "None",
            "intended_measurement_Iso": "control",
        },
        {
            "fiber_name": "Fiber_2",
            "intended_measurement_R": "acetylcholine",
            "intended_measurement_G": "dopamine",
            "intended_measurement_B": "None",
            "intended_measurement_Iso": "control",
        },
        {
            "fiber_name": "Fiber_3",
            "intended_measurement_R": "acetylcholine",
            "intended_measurement_G": "dopamine",
            "intended_measurement_B": "None",
            "intended_measurement_Iso": "control",
        },
    ],
}

URLOPEN = "aind_physiology_fip_data_mappers._intended_measurements.urllib.request.urlopen"


@pytest.fixture(autouse=True)
def _no_retry_delay():
    """Retries wait between attempts, which the tests skip."""
    with patch("aind_physiology_fip_data_mappers._intended_measurements.time.sleep"):
        yield


def _ok(status: int, body: dict) -> MagicMock:
    """A response urlopen returns for a 2xx status."""
    response = MagicMock(status=status)
    response.read.return_value = json.dumps(body).encode()
    response.__enter__.return_value = response
    return response


def _http_error(status: int, body: dict) -> urllib.error.HTTPError:
    """The error urlopen raises for a status outside 2xx, carrying the response body."""
    return urllib.error.HTTPError("http://service", status, "", None, io.BytesIO(json.dumps(body).encode()))  # type: ignore[arg-type]


class TestParse:
    def test_reads_each_fibers_channels(self):
        """Each fiber's green, isosbestic and red measurements are read by channel key"""
        parsed = parse_intended_measurements(RESPONSE)

        assert sorted(parsed) == ["Fiber_0", "Fiber_1", "Fiber_2", "Fiber_3"]
        assert parsed["Fiber_0"] == {"Green": "serotonin", "Isosbestic": "control", "Red": "acetylcholine"}

    def test_none_string_is_no_measurement(self):
        """The service writes a channel with nothing to measure as the string "None\""""
        assert parse_intended_measurements(RESPONSE)["Fiber_1"]["Red"] is None

    def test_blue_is_not_read(self):
        """FIP has no blue channel, so the blue measurement is dropped"""
        assert all("B" not in channels for channels in parse_intended_measurements(RESPONSE).values())

    def test_single_row(self):
        """A subject with one fiber comes back as a single row rather than a list"""
        parsed = parse_intended_measurements({"data": RESPONSE["data"][0]})
        assert list(parsed) == ["Fiber_0"]

    def test_row_without_a_fiber_name_is_skipped(self):
        """A row that names no fiber cannot be matched to a patch cord"""
        parsed = parse_intended_measurements({"data": [*RESPONSE["data"][:1], {"intended_measurement_G": "dopamine"}]})
        assert list(parsed) == ["Fiber_0"]

    @pytest.mark.parametrize("data", [None, [], [{"intended_measurement_G": "dopamine"}]])
    def test_response_without_a_fiber_is_rejected(self, data):
        """A response with no fiber has nothing to map"""
        with pytest.raises(ValueError, match="no fiber"):
            parse_intended_measurements({"data": data})


class TestFetch:
    def test_reads_a_multiple_fiber_response(self):
        """The service answers 300 for several fibers, which urllib raises but which carries the data"""
        with patch(URLOPEN, side_effect=_http_error(300, RESPONSE)) as urlopen:
            parsed = fetch_intended_measurements("876201")

        assert urlopen.call_args.args[0] == f"{METADATA_SERVICE_URL}/intended_measurements/876201"
        assert parsed["Fiber_2"]["Green"] == "dopamine"

    def test_reads_a_single_fiber_response(self):
        """A subject with one fiber is answered with a 200"""
        with patch(URLOPEN, return_value=_ok(200, {"data": RESPONSE["data"][0]})):
            assert list(fetch_intended_measurements("876201")) == ["Fiber_0"]

    def test_unknown_subject_is_rejected(self):
        """A subject the service has no measurements for fails the lookup"""
        not_found = _http_error(404, {"message": "No Data Found.", "data": None})
        with (
            patch(URLOPEN, side_effect=not_found),
            pytest.raises(ValueError, match="No intended measurements for subject 000000"),
        ):
            fetch_intended_measurements("000000")

    def test_service_error_is_rejected(self):
        """An error from the service fails the lookup with its status"""
        server_error = _http_error(500, {"message": "Internal Server Error"})
        with patch(URLOPEN, side_effect=server_error), pytest.raises(ValueError, match="500"):
            fetch_intended_measurements("876201")

    def test_unreadable_response_is_rejected(self):
        """A response that is not the expected JSON fails the lookup"""
        response = _ok(200, {})
        response.read.return_value = b"<html>not json</html>"
        with patch(URLOPEN, return_value=response), pytest.raises(ValueError):
            fetch_intended_measurements("876201")

    def test_retries_when_the_service_cannot_be_reached(self):
        """A dropped connection is retried before the lookup succeeds"""
        with patch(URLOPEN, side_effect=[urllib.error.URLError("refused"), _http_error(300, RESPONSE)]) as urlopen:
            parsed = fetch_intended_measurements("876201")

        assert urlopen.call_count == 2
        assert "Fiber_0" in parsed

    def test_fails_when_the_service_stays_unreachable(self):
        """Once every attempt fails to connect, the last error is raised"""
        with (
            patch(URLOPEN, side_effect=urllib.error.URLError("refused")) as urlopen,
            pytest.raises(urllib.error.URLError),
        ):
            fetch_intended_measurements("876201")

        assert urlopen.call_count == 3

    def test_error_status_is_not_retried(self):
        """The service answering with an error is a real answer, so it is not asked again"""
        with patch(URLOPEN, side_effect=_http_error(500, {})) as urlopen, pytest.raises(ValueError):
            fetch_intended_measurements("876201")

        assert urlopen.call_count == 1

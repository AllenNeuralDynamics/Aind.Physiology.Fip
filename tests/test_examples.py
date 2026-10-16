"""testing examples"""

import glob

import pytest

from tests import EXAMPLES_DIR, build_example


@pytest.mark.parametrize("script_path", sorted(glob.glob(str(EXAMPLES_DIR / "*.py"))))
def test_examples(script_path):
    """Each script in the examples folder executes without raising."""
    build_example(script_path)  # raises if the example fails to execute

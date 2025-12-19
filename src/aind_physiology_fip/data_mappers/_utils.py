import enum

class TrackedDevicesInfo(enum.StrEnum):
    COMPUTER = "computer"
    LENS_NAME = "Image focusing lens"
    LENS_MODEL = "AC254-080-A-ML"
    WHITE_RABBIT_DEVICE_NAME = "harp_clock_generator"
    DETECTOR_BIN_WIDTH = 4
    DETECTOR_BIN_HEIGHT = 4
    DETECTOR_MODEL = "BFS-U3-20S40M"
    DETECTOR_CROP_WIDTH = 200
    DETECTOR_CROP_HEIGHT = 200
    DETECTOR_BIT_DEPTH = 16
    PORT_COM = "COM14"
    PORT_CLOCK = "ClkOut"
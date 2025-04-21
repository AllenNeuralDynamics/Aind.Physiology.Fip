using System.ComponentModel;
using OpenCV.Net;

namespace FipExtensions
{
    [Description("Represents a camera frame from a FIP.")]
    public class FipFrame
    {
        public IplImage Image { get; set; }
        public FipCameraSource Source { get; set; }
        public long FrameNumber { get; set; }
        public long FrameTime { get; set; }
    }

    public enum FipCameraSource
    {
        Iso = 0,
        Green = 1,
        Red = 2,
    }
}
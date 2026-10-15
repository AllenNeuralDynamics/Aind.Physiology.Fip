using System.Collections.Generic;
using System.Numerics;
using FipExtensions;
using OpenCV.Net;

/// <summary>
/// Provides helper methods for converting OpenCV.Net color scalars into Hexa.NET.ImGui/ImPlot
/// color vectors, plus hard-coded color/name lookups for Fip camera channels.
/// </summary>
static class ColorExtensions
{
    /// <summary>
    /// Converts an OpenCV.Net.Scalar, as used by Bonsai.Vision's color pickers (BGRA, 0-255 per
    /// channel), into the RGBA (0-1 per channel) vector expected by Hexa.NET.ImGui/ImPlot.
    /// </summary>
    public static Vector4 ToVector4(this Scalar color)
    {
        return new Vector4(
            (float)color.Val2 / 255f,
            (float)color.Val1 / 255f,
            (float)color.Val0 / 255f,
            (float)color.Val3 / 255f);
    }

    private static readonly Vector4 defaultColor = new Vector4(0.7f, 0.7f, 0.7f, 1f);

    // Colored by the camera/emission channel it records (Iso/Green/Red).
    private static readonly Dictionary<FipCameraSource, Vector4> cameraColors = new Dictionary<FipCameraSource, Vector4>
    {
        { FipCameraSource.Iso, new Vector4(0.12f, 0.56f, 1.00f, 1f) },
        { FipCameraSource.Green, new Vector4(0.20f, 0.80f, 0.20f, 1f) },
        { FipCameraSource.Red, new Vector4(0.90f, 0.15f, 0.15f, 1f) },
    };

    // Colored by the LED that illuminates that channel (Uv/Blue/Lime).
    private static readonly Dictionary<FipCameraSource, Vector4> lightSourceColors = new Dictionary<FipCameraSource, Vector4>
    {
        { FipCameraSource.Iso, new Vector4(0.58f, 0.20f, 0.92f, 1f) },
        { FipCameraSource.Green, new Vector4(0.12f, 0.56f, 1.00f, 1f) },
        { FipCameraSource.Red, new Vector4(0.60f, 0.80f, 0.20f, 1f) },
    };

    // Fixed hardware pairing: each camera channel is always driven by the same LED.
    private static readonly Dictionary<FipCameraSource, string> lightSourceNames = new Dictionary<FipCameraSource, string>
    {
        { FipCameraSource.Iso, "Uv" },
        { FipCameraSource.Green, "Blue" },
        { FipCameraSource.Red, "Lime" },
    };

    // Display name used for camera channels in plots/tables (distinct from the enum's own name).
    private static readonly Dictionary<FipCameraSource, string> cameraDisplayNames = new Dictionary<FipCameraSource, string>
    {
        { FipCameraSource.Iso, "Isosbestic" },
        { FipCameraSource.Green, "Green" },
        { FipCameraSource.Red, "Red" },
    };

    // Fixed per-Roi colors, indexed by Roi number (0 = first Roi after the Background), shared by
    // the Roi picker and the activity plots. Roi numbers past the end cycle back to the start.
    private static readonly Vector4[] roiColors =
    {
        Rgb(0xFF, 0x32, 0x32), // ROI 0: red
        Rgb(0x32, 0x64, 0xFF), // ROI 1: blue
        Rgb(0x96, 0x32, 0xFF), // ROI 2: purple
        Rgb(0xFF, 0xD7, 0x00), // ROI 3: yellow
        Rgb(0xFF, 0x8C, 0x00), // ROI 4: orange
        Rgb(0x00, 0xD2, 0xFF), // ROI 5: cyan
        Rgb(0xFF, 0x50, 0xB4), // ROI 6: pink
        Rgb(0xA0, 0x52, 0x2D), // ROI 7: brown
    };

    private static readonly Vector4 backgroundColor = Rgb(0xFF, 0xFF, 0xFF);

    private static Vector4 Rgb(int r, int g, int b)
    {
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }

    public static Vector4 RoiColor(int roiNumber)
    {
        var count = roiColors.Length;
        return roiColors[((roiNumber % count) + count) % count];
    }

    public static Vector4 BackgroundColor
    {
        get { return backgroundColor; }
    }

    public static Vector4 CameraColor(FipCameraSource camera)
    {
        Vector4 color;
        return cameraColors.TryGetValue(camera, out color) ? color : defaultColor;
    }

    public static string CameraDisplayName(FipCameraSource camera)
    {
        string name;
        return cameraDisplayNames.TryGetValue(camera, out name) ? name : camera.ToString();
    }

    public static Vector4 LightSourceColor(FipCameraSource camera)
    {
        Vector4 color;
        return lightSourceColors.TryGetValue(camera, out color) ? color : defaultColor;
    }

    public static string LightSourceName(FipCameraSource camera)
    {
        string name;
        return lightSourceNames.TryGetValue(camera, out name) ? name : camera.ToString();
    }
}

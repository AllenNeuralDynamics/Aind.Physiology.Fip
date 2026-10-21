using System;
using System.Numerics;
using Hexa.NET.ImGui;

/// <summary>
/// ImGui helpers shared by the Fip power visualizers: colors, auto font sizing, stretched table
/// rows and centered cell text.
/// </summary>
static class FipPowerUi
{
    public static readonly Vector4 ActiveColor = new Vector4(0.20f, 0.80f, 0.20f, 1f);
    public static readonly Vector4 InactiveColor = new Vector4(0.35f, 0.35f, 0.35f, 0.6f);
    public static readonly Vector4 StateOnColor = new Vector4(0.15f, 0.45f, 0.90f, 1f);
    public static readonly Vector4 StateOffColor = new Vector4(0.35f, 0.35f, 0.35f, 1f);

    // Row height of the table being drawn; rows are stretched so the table fills the window.
    public static float RowHeight;

    /// <summary>
    /// Pushes the current font at <paramref name="size"/> pixels, compensating for the context-wide
    /// font scale set by <see cref="FipTabScale"/> so the content size doesn't depend on it.
    /// </summary>
    public static void PushFont(float size)
    {
        // ImGuiStylePtr.FontScaleMain isn't supported by Bonsai's script-extensions compiler (CS0570).
        float scale;
        unsafe { scale = ImGui.GetStyle().Handle->FontScaleMain; }
        ImGui.PushFont(ImGui.GetFont(), size / Math.Max(0.25f, scale));
    }

    /// <summary>
    /// Picks a font size so <paramref name="rowCount"/> rows of <paramref name="linesPerRow"/> text
    /// lines (plus a header) fit the free height, and <paramref name="widthLines"/> text lines fit
    /// the free width.
    /// </summary>
    public static float AutoFontSize(int rowCount, float linesPerRow, float widthLines)
    {
        var avail = ImGui.GetContentRegionAvail();
        var byHeight = avail.Y / (linesPerRow * (Math.Max(1, rowCount) + 1));
        var byWidth = avail.X / widthLines;
        return Math.Max(12f, Math.Min(60f, Math.Min(byHeight, byWidth)));
    }

    /// <summary>
    /// Splits the remaining vertical space evenly across the rows, leaving room for the header,
    /// and stores the result in <see cref="RowHeight"/>.
    /// </summary>
    public static float ComputeRowHeight(int rowCount)
    {
        var header = ImGui.GetFrameHeight();
        var available = ImGui.GetContentRegionAvail().Y - header - 6f;
        RowHeight = Math.Max(header, available / Math.Max(1, rowCount));
        return RowHeight;
    }

    /// <summary>Moves the cursor down so content of the given height sits in the middle of the row.</summary>
    public static void VerticalCenter(float contentHeight)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (RowHeight - contentHeight) / 2f - 4f));
    }

    /// <summary>Centers text in the current table cell, horizontally and vertically. Null color = default.</summary>
    public static void CenteredText(string text, Vector4? color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.AlignTextToFramePadding();
        VerticalCenter(ImGui.GetFrameHeight());
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - width) / 2f));
        if (color.HasValue) ImGui.TextColored(color.Value, text);
        else ImGui.TextUnformatted(text);
    }

    /// <summary>True when the light source has a power calibration, i.e. power is expressed in microwatts.</summary>
    public static bool IsCalibrated(CalibratedLightSource lightSource)
    {
        return lightSource != null && lightSource.LightSource != null && lightSource.LightSource.Calibration != null;
    }

    // Uncalibrated DutyCycleToPower is just the unity LUT, so show that case as a percentage
    // rather than fake microwatts. Calibration LUTs are stored directly in microwatts.
    public static string FormatPower(CalibratedLightSource lightSource, float dutyCycle)
    {
        if (lightSource == null) return "-";
        var realPower = lightSource.DutyCycleToPower.Interpolate(dutyCycle);
        return IsCalibrated(lightSource)
            ? realPower.ToString("F1") + " µW"
            : (realPower * 100).ToString("F1") + "%";
    }
}

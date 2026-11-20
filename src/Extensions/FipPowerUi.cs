using System;
using System.Numerics;
using Hexa.NET.ImGui;

/// <summary>
/// UI lock shared by the ROI manager (which owns the Locked/Unlocked button) and the experiment
/// power control. Locked at startup so neither can be changed by accident.
/// </summary>
static class FipUiLock
{
    static volatile bool locked = true;
    public static bool Locked { get { return locked; } set { locked = value; } }
}

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
    // Marks a slider value that has not been sent to the hardware yet.
    public static readonly Vector4 PendingColor = new Vector4(0.95f, 0.65f, 0.10f, 1f);

    // Row height of the table being drawn; rows are stretched so the table fills the window.
    public static float RowHeight;

    /// <summary>
    /// Pushes the current font at <paramref name="size"/> screen pixels, compensating for the
    /// context-wide font scales (<see cref="FipTabScale"/> zoom and <see cref="FipDpi"/> DPI) so the
    /// content size doesn't depend on them.
    /// </summary>
    public static void PushFont(float size)
    {
        // ImGuiStylePtr.FontScaleMain isn't supported by Bonsai's script-extensions compiler (CS0570).
        float scale;
        unsafe { scale = ImGui.GetStyle().Handle->FontScaleMain; }
        ImGui.PushFont(ImGui.GetFont(), size / (Math.Max(0.25f, scale) * FipDpi.Scale));
    }

    /// <summary>
    /// Picks a font size so <paramref name="rowCount"/> table rows of <paramref name="linesPerRow"/>
    /// text lines (plus a header) fit the free height, and <paramref name="widthLines"/> text lines
    /// fit the free width. Accounts for the font's line height (Segoe UI lines are ~1.33x the font
    /// size) and the frame/cell padding, which doesn't grow with the font. The result is in screen
    /// pixels, clamped to 10-20 pixels at 96 DPI so the text stays at a normal reading size.
    /// </summary>
    public static float AutoFontSize(int rowCount, float linesPerRow, float widthLines)
    {
        var avail = ImGui.GetContentRegionAvail();
        var lineRatio = ImGui.GetTextLineHeight() / Math.Max(1f, ImGui.GetFontSize());
        float padding;
        unsafe
        {
            var style = ImGui.GetStyle().Handle;
            padding = 2f * (style->FramePadding.Y + style->CellPadding.Y) + 1f;
        }
        var rows = Math.Max(1, rowCount) + 1;
        var perRow = (avail.Y - FipDpi.Px(6f)) / rows - padding;
        var byHeight = perRow / (linesPerRow * lineRatio);
        var byWidth = avail.X / widthLines;
        return Math.Max(FipDpi.Px(10f), Math.Min(FipDpi.Px(20f), Math.Min(byHeight, byWidth)));
    }

    /// <summary>
    /// Splits the remaining vertical space evenly across the rows, leaving room for the header,
    /// and stores the result in <see cref="RowHeight"/>.
    /// </summary>
    public static float ComputeRowHeight(int rowCount)
    {
        var header = ImGui.GetFrameHeight();
        var available = ImGui.GetContentRegionAvail().Y - header - FipDpi.Px(6f);
        RowHeight = Math.Max(header, available / Math.Max(1, rowCount));
        return RowHeight;
    }

    /// <summary>Moves the cursor down so content of the given height sits in the middle of the row.</summary>
    public static void VerticalCenter(float contentHeight)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (RowHeight - contentHeight) / 2f - FipDpi.Px(4f)));
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

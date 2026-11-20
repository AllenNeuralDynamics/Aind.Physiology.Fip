using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;

/// <summary>
/// Scales the current ImGui context to the monitor DPI, following the Dear ImGui recipe:
/// <c>style.ScaleAllSizes(s)</c> for padding/spacing/rounding and <c>style.FontScaleDpi = s</c>
/// for fonts. Sizes in the Fip visualizers are written for 96 DPI (100%) and multiplied by
/// <see cref="Scale"/>, or derived from the font/frame size, which already include it.
/// </summary>
/// <remarks>
/// Bonsai.exe is system-DPI aware (not per-monitor), so the DPI is the one Windows had at
/// sign-in; on a monitor with a different scale Windows stretches the window instead.
/// The scale lives in the context's style, so each ImGui control is scaled independently and
/// <see cref="Apply"/> is a cheap no-op once applied. Style sizes and fields are accessed through
/// <c>Handle-&gt;</c> because Bonsai's script-extensions compiler rejects the ref-returning
/// <c>ImGuiStylePtr</c> properties (CS0570).
/// </remarks>
static class FipDpi
{
    const float BaseDpi = 96f;

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    /// <summary>DPI scale of the current ImGui context: 1 at 96 DPI (100%), 1.5 at 144 DPI (150%).</summary>
    public static float Scale
    {
        get { unsafe { return Math.Max(0.25f, ImGui.GetStyle().Handle->FontScaleDpi); } }
    }

    /// <summary>Style item spacing, already DPI-scaled.</summary>
    public static Vector2 ItemSpacing
    {
        get { unsafe { return ImGui.GetStyle().Handle->ItemSpacing; } }
    }

    /// <summary>Style frame padding, already DPI-scaled.</summary>
    public static Vector2 FramePadding
    {
        get { unsafe { return ImGui.GetStyle().Handle->FramePadding; } }
    }

    /// <summary>
    /// Scales the current context's style to the window DPI if it isn't already. Call once per
    /// frame, before pushing any style var (a pop would restore the unscaled value).
    /// </summary>
    public static void Apply()
    {
        var target = Query();
        unsafe
        {
            var style = ImGui.GetStyle();
            var current = Math.Max(0.25f, style.Handle->FontScaleDpi);
            if (Math.Abs(target - current) < 0.01f) return;
            // ScaleAllSizes multiplies whatever is there, so scale by the ratio to the current DPI.
            style.ScaleAllSizes(target / current);
            style.Handle->FontScaleDpi = target;
        }
    }

    /// <summary>A length given in 96-DPI pixels, converted to screen pixels.</summary>
    public static float Px(float logicalPixels)
    {
        return logicalPixels * Scale;
    }

    /// <summary>
    /// Width of a frame (button, input) wide enough for <paramref name="text"/> in the current
    /// font, and at least <paramref name="minLogicalWidth"/> 96-DPI pixels.
    /// </summary>
    public static float FitWidth(string text, float minLogicalWidth)
    {
        return Math.Max(Px(minLogicalWidth), ImGui.CalcTextSize(text).X + 2f * FramePadding.X);
    }

    static float Query()
    {
        uint dpi = 0;
        try
        {
            IntPtr hwnd;
            // The Win32 backend stores the host window handle in the main viewport.
            unsafe { hwnd = (IntPtr)ImGui.GetMainViewport().Handle->PlatformHandleRaw; }
            dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : GetDpiForSystem();
        }
        catch (EntryPointNotFoundException)
        {
            // Windows older than 10 (1607): leave unscaled.
        }
        return dpi > 0 ? dpi / BaseDpi : 1f;
    }
}

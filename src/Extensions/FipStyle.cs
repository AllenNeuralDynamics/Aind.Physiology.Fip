using System;
using System.IO;
using Hexa.NET.ImGui;

/// <summary>
/// One-time look of an ImGui context hosting Fip visualizers: Segoe UI as the default font
/// (crisp at any size, unlike the built-in pixel font), a more compact style with rounded frames,
/// and then the monitor DPI scale (<see cref="FipDpi.Apply"/>).
/// </summary>
/// <remarks>
/// Call once per frame at the start of each visualizer's draw, before pushing any style var.
/// Dear ImGui 1.92 with the OpenGL3 backend builds font textures on demand, so a font can be
/// added mid-frame; it becomes the default from the next frame. The context is marked as styled
/// through <c>io.UserData</c>, which Bonsai.ImGui doesn't use, so the style is set only once
/// per context, in unscaled sizes, before the DPI scale multiplies it.
/// </remarks>
static class FipStyle
{
    const float FontSize = 13f;
    static readonly string FontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
    // Arbitrary tag stored in io.UserData ("FIPS").
    static readonly IntPtr StyledMarker = new IntPtr(0x46495053);

    public static void Apply()
    {
        unsafe
        {
            // ImGuiIO/ImGuiStyle pointers can't be named as types under C# 7.3 (they hold generic
            // ImVector fields), so they are always reached through Handle.
            if ((IntPtr)ImGui.GetIO().Handle->UserData != StyledMarker)
            {
                ImGui.GetIO().Handle->UserData = (void*)StyledMarker;
                SetStyle();
                LoadFont();
            }
        }
        FipDpi.Apply();
    }

    // Sizes at 96 DPI; FipDpi.Apply scales them afterwards.
    static unsafe void SetStyle()
    {
        var style = ImGui.GetStyle().Handle;
        style->WindowPadding = new System.Numerics.Vector2(6f, 6f);
        style->FramePadding = new System.Numerics.Vector2(6f, 3f);
        style->ItemSpacing = new System.Numerics.Vector2(6f, 4f);
        style->ItemInnerSpacing = new System.Numerics.Vector2(4f, 4f);
        style->CellPadding = new System.Numerics.Vector2(4f, 2f);
        style->ScrollbarSize = 12f;
        style->GrabMinSize = 10f;
        style->FrameRounding = 3f;
        style->GrabRounding = 3f;
        style->PopupRounding = 3f;
        style->TabRounding = 3f;
        style->ChildRounding = 3f;
    }

    static unsafe void LoadFont()
    {
        if (!File.Exists(FontPath)) return; // keep the built-in font
        // ImGuiIOPtr.Fonts is a ref property, which Bonsai's script compiler rejects (CS0570).
        var fonts = new ImFontAtlasPtr(ImGui.GetIO().Handle->Fonts);
        var font = fonts.AddFontFromFileTTF(FontPath, FontSize);
        if (font != null) ImGui.GetIO().Handle->FontDefault = font;
    }
}

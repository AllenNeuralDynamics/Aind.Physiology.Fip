using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Xml.Serialization;
using AindPhysiologyFip;
using Bonsai;
using FipExtensions;
using Hexa.NET.ImGui;
using Newtonsoft.Json;
using OpenCV.Net;
using OpenTK.Graphics.OpenGL4;

/// <summary>
/// Represents an operator that lets the user interactively pick the Background + ROI circles
/// for the Iso/Green/Red camera images, and persists that selection to/from disk. Source1 =
/// Frame subject; Source2 = the same grouped Fip activity stream consumed by
/// <see cref="FipActivityGridVisualizer"/> (e.g. ParsedFipStreams), used here only to obtain a
/// live preview image per camera.
/// </summary>
/// <remarks>
/// Renders one plain-ImGui column per physical camera source (Iso, Green, Red) with its own raw
/// image; Iso and Green share the same underlying ROI geometry (they're the same optics/sensor,
/// just different exposures), so dragging or resizing a circle in either column moves it in both
/// ("yoked"). Red has its own independent circles. Emits the current
/// <see cref="AindPhysiologyFip.RoiSettings"/> whenever a circle is dragged/resized or a
/// Load/Reset action completes. "Save Roi"/"Load Roi"/"Reset Roi" each require confirming an
/// inline popup before acting. <see cref="RoiSettings"/> (the property) is only consulted once,
/// to seed the initial selection if set (e.g. via PropertyMapping from a rig schema); "Load Roi"
/// always re-reads from <see cref="LocalRoiDefaultPath"/> instead.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders a live-image ROI picker with one column per camera (Iso, Green, Red; Iso/Green share yoked ROIs), plus Save Roi/Load Roi/Reset Roi buttons (each behind a confirm popup), emitting the current RoiSettings whenever it changes. Source1 = Frame subject; Source2 = the grouped Fip activity stream (e.g. ParsedFipStreams), used only for the live preview images.")]
public class RoiManagerVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private bool enabled = true;
    [Description("Specifies whether Save/Load/Reset and circle drag/resize are enabled. When false, everything is read-only, but circles still highlight green on hover.")]
    public bool Enabled { get { return enabled; } set { enabled = value; } }

    private float fontSize = 20f;
    [Description("Font size used to render the control.")]
    public float FontSize { get { return fontSize; } set { fontSize = value; } }

    private string localRoiDefaultPath = "../local/default.json";
    [Description("The path used by Save Roi/Load Roi to persist the ROI configuration.")]
    [FileNameFilter("JSON|*.json|All Files|*.*")]
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    public string LocalRoiDefaultPath { get { return localRoiDefaultPath; } set { localRoiDefaultPath = value; } }

    /// <summary>
    /// Gets or sets the ROI settings used to seed the initial selection, if set. Consulted only
    /// once, when this operator first starts rendering; not serialized, assign via PropertyMapping
    /// (e.g. from a rig schema) rather than the property grid.
    /// </summary>
    [XmlIgnore]
    public RoiSettings RoiSettings { get; set; }

    static readonly FipCameraSource[] cameraOrder = { FipCameraSource.Iso, FipCameraSource.Green, FipCameraSource.Red };

    const float minRadius = 2f;
    const float radiusStep = 2f;
    const float circleFillAlpha = 0.18f;
    const float circleStrokeThickness = 2f;
    static readonly Vector4 inactiveCircleColor = new Vector4(0.9f, 0.15f, 0.15f, 1f);
    static readonly Vector4 activeCircleColor = new Vector4(0.15f, 0.85f, 0.15f, 1f);
    static readonly Vector4 labelColor = new Vector4(1f, 1f, 1f, 1f);

    class PickerCircle
    {
        public float X;
        public float Y;
        public float Radius;
    }

    class RoiCircleSet
    {
        public PickerCircle Background;
        public List<PickerCircle> Rois;
    }

    class ImageSlot
    {
        public FipCameraSource Camera;
        public string Label;
        public RoiCircleSet Circles;
        public int TextureId;
        public ImTextureRef TexRef;
        public int TextureWidth;
        public int TextureHeight;
        public IplImage PendingImage;
        public bool ImageDirty;
    }

    static PickerCircle MakeCircle(float x, float y, float radius)
    {
        return new PickerCircle { X = x, Y = y, Radius = radius };
    }

    // -1 is the Background circle; 0.. are the Rois list indices. Purely positional - deleting
    // an Roi renumbers everything after it, same as the channel-visualizer naming convention.
    static string RoiName(int index)
    {
        return index < 0 ? "Background" : "ROI" + index;
    }

    // Matches the short label drawn on the circle itself in the image overlay.
    static string RoiIndexLabel(int index)
    {
        return index < 0 ? "B" : index.ToString();
    }

    static List<PickerCircle> DefaultRois()
    {
        var offsets = new float[] { 50, 150 };
        var rois = new List<PickerCircle>();
        foreach (var x in offsets)
        {
            foreach (var y in offsets)
            {
                rois.Add(MakeCircle(x, y, 20));
            }
        }
        return rois;
    }

    static RoiCircleSet MakeDefaultCircleSet()
    {
        return new RoiCircleSet { Background = MakeCircle(0, 0, 20), Rois = DefaultRois() };
    }

    static void ResetToDefault(RoiCircleSet set)
    {
        set.Background = MakeCircle(0, 0, 20);
        set.Rois = DefaultRois();
    }

    static Circle ToFipCircle(PickerCircle c)
    {
        return new Circle { Center = new AindPhysiologyFip.Point2f { X = c.X, Y = c.Y }, Radius = c.Radius };
    }

    static PickerCircle FromFipCircle(Circle c, float fallbackX, float fallbackY, float fallbackRadius)
    {
        if (c == null) return MakeCircle(fallbackX, fallbackY, fallbackRadius);
        return MakeCircle((float)c.Center.X, (float)c.Center.Y, (float)c.Radius);
    }

    static void ApplySettings(RoiCircleSet set, Circle background, List<Circle> rois)
    {
        set.Background = FromFipCircle(background, 0, 0, 20);
        var defaults = DefaultRois();
        if (rois == null || rois.Count == 0)
        {
            set.Rois = defaults;
            return;
        }

        var result = new List<PickerCircle>();
        for (int i = 0; i < rois.Count; i++)
        {
            var fallback = i < defaults.Count ? defaults[i] : defaults[defaults.Count - 1];
            result.Add(FromFipCircle(rois[i], fallback.X, fallback.Y, fallback.Radius));
        }
        set.Rois = result;
    }

    static RoiSettings ToRoiSettings(RoiCircleSet greenIso, RoiCircleSet red)
    {
        return new RoiSettings
        {
            CameraGreenIsoBackground = ToFipCircle(greenIso.Background),
            CameraGreenIsoRoi = greenIso.Rois.Select(ToFipCircle).ToList(),
            CameraRedBackground = ToFipCircle(red.Background),
            CameraRedRoi = red.Rois.Select(ToFipCircle).ToList(),
        };
    }

    public IObservable<RoiSettings> Process<TTickSource>(
        IObservable<TTickSource> frames,
        IObservable<System.Reactive.Linq.IGroupedObservable<FipCameraSource, Bonsai.Harp.Timestamped<CircleActivityCollection>>> streams)
    {
        return Observable.Create<RoiSettings>(observer =>
        {
            var greenIso = MakeDefaultCircleSet();
            var red = MakeDefaultCircleSet();
            var slots = new List<ImageSlot>();
            for (int i = 0; i < cameraOrder.Length; i++)
            {
                var camera = cameraOrder[i];
                slots.Add(new ImageSlot
                {
                    Camera = camera,
                    Label = ColorExtensions.CameraDisplayName(camera),
                    Circles = camera == FipCameraSource.Red ? red : greenIso,
                });
            }

            var imageLock = new object();
            var seeded = false;
            var innerSubs = new CompositeDisposable();
            // Which circle key (bg/roi0../roi3) was hovered/dragged last frame, across ALL
            // columns; used to color that circle green everywhere this frame ("yoked" feedback).
            var previousActiveKeys = new HashSet<string>();
            // Which circle keys are explicitly selected via the Roi table below the images;
            // persists across frames (unlike previousActiveKeys) until toggled off or deleted.
            var selectedKeys = new HashSet<string>();

            var streamsSub = streams.SubscribeSafe(Observer.Create<System.Reactive.Linq.IGroupedObservable<FipCameraSource, Bonsai.Harp.Timestamped<CircleActivityCollection>>>(
                group =>
                {
                    var slot = slots.FirstOrDefault(s => s.Camera == group.Key);
                    if (slot == null) return;

                    var innerSub = group.SubscribeSafe(Observer.Create<Bonsai.Harp.Timestamped<CircleActivityCollection>>(
                        activity =>
                        {
                            var image = activity.Value.FipFrame.Image;
                            lock (imageLock)
                            {
                                slot.PendingImage = image;
                                slot.ImageDirty = true;
                            }
                        },
                        observer.OnError));
                    innerSubs.Add(innerSub);
                },
                observer.OnError,
                observer.OnCompleted));

            var frameSub = frames.SubscribeSafe(Observer.Create<TTickSource>(
                _ =>
                {
                    // Disable native assertions for recoverable ImGui errors
                    // (mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0).
                    unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }

                    if (!Visible) return;

                    var changed = false;
                    if (!seeded)
                    {
                        var seed = RoiSettings;
                        if (seed != null)
                        {
                            ApplySettings(greenIso, seed.CameraGreenIsoBackground, seed.CameraGreenIsoRoi);
                            ApplySettings(red, seed.CameraRedBackground, seed.CameraRedRoi);
                        }
                        seeded = true;
                        changed = true;
                    }

                    foreach (var slot in slots) UpdateTexture(slot, imageLock);

                    ImGui.PushFont(ImGui.GetFont(), FontSize);

                    // Fixed-size child + AlwaysVerticalScrollbar keep our size and scrollbar
                    // gutter stable across frames - otherwise small content-height changes
                    // (image scaling, Roi table row count) flip a scrollbar on/off, which
                    // itself changes the available width, causing visible jitter.
                    ImGui.BeginChild("##RoiManagerCanvas", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);

                    ImGui.BeginDisabled(!Enabled);
                    RenderControls(greenIso, red, ref changed);
                    ImGui.EndDisabled();

                    var currentActiveKeys = new HashSet<string>();
                    changed |= RenderCameras(slots, previousActiveKeys, currentActiveKeys, selectedKeys, Enabled);
                    previousActiveKeys = currentActiveKeys;

                    ImGui.TextDisabled("Drag a circle to move it  -  Scroll while hovering it to resize");

                    ImGui.EndChild();

                    ImGui.PopFont();

                    if (changed) observer.OnNext(ToRoiSettings(greenIso, red));
                },
                observer.OnError,
                observer.OnCompleted));

            return new CompositeDisposable(streamsSub, innerSubs, frameSub);
        });
    }

    void RenderControls(RoiCircleSet greenIso, RoiCircleSet red, ref bool changed)
    {
        var buttonSize = new Vector2(140f, ImGui.GetFrameHeight() * 1.6f);

        if (ImGui.Button("Save Roi##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmSaveRoi##RoiManager");
        if (ConfirmPopup("ConfirmSaveRoi##RoiManager", "Overwrite the saved ROI configuration on disk?"))
        {
            SaveToDisk(greenIso, red);
        }

        ImGui.SameLine();
        if (ImGui.Button("Load Roi##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmLoadRoi##RoiManager");
        if (ConfirmPopup("ConfirmLoadRoi##RoiManager", "Load the cached ROI configuration? This overwrites the current selection."))
        {
            if (LoadFromDisk(greenIso, red)) changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset Roi##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmResetRoi##RoiManager");
        if (ConfirmPopup("ConfirmResetRoi##RoiManager", "Reset to the default ROI configuration?"))
        {
            ResetToDefault(greenIso);
            ResetToDefault(red);
            changed = true;
        }

        // Non-interactive warning badge: appears when the two cameras have different ROI counts.
        var greenRoiCount = greenIso.Rois.Count;
        var redRoiCount = red.Rois.Count;
        if (greenRoiCount != redRoiCount)
        {
            ImGui.SameLine();
            var warningColor = new Vector4(0.80f, 0.08f, 0.08f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, warningColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, warningColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, warningColor);
            ImGui.Button(
                "⚠ ROI mismatch: Green/Iso has " + greenRoiCount + ", Red has " + redRoiCount + "##RoiMismatch",
                new Vector2(0f, buttonSize.Y));
            ImGui.PopStyleColor(3);
        }
    }

    static bool ConfirmPopup(string id, string message)
    {
        var confirmed = false;
        if (ImGui.BeginPopup(id))
        {
            ImGui.TextUnformatted(message);
            if (ImGui.Button("Yes##" + id))
            {
                confirmed = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("No##" + id))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        return confirmed;
    }

    void SaveToDisk(RoiCircleSet greenIso, RoiCircleSet red)
    {
        var settings = ToRoiSettings(greenIso, red);
        var json = JsonConvert.SerializeObject(settings);
        var directory = Path.GetDirectoryName(LocalRoiDefaultPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(LocalRoiDefaultPath, json);
    }

    bool LoadFromDisk(RoiCircleSet greenIso, RoiCircleSet red)
    {
        if (!File.Exists(LocalRoiDefaultPath)) return false;
        var json = File.ReadAllText(LocalRoiDefaultPath);
        var settings = JsonConvert.DeserializeObject<RoiSettings>(json);
        if (settings == null) return false;
        ApplySettings(greenIso, settings.CameraGreenIsoBackground, settings.CameraGreenIsoRoi);
        ApplySettings(red, settings.CameraRedBackground, settings.CameraRedRoi);
        return true;
    }

    static bool RenderCameras(List<ImageSlot> slots, HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = false;
        if (ImGui.BeginTable("##RoiManagerCameras", slots.Count, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            foreach (var slot in slots)
            {
                ImGui.TableNextColumn();
                RenderCameraLabel(slot);
            }

            ImGui.TableNextRow();
            foreach (var slot in slots)
            {
                ImGui.TableNextColumn();
                changed |= RenderCameraImage(slot, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
            }

            ImGui.TableNextRow();
            foreach (var slot in slots)
            {
                ImGui.TableNextColumn();
                changed |= RenderRoiTable(slot, selectedKeys, enabled);
            }

            ImGui.EndTable();
        }
        return changed;
    }

    static void RenderCameraLabel(ImageSlot slot)
    {
        var availWidth = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(slot.Label);
        var offset = Math.Max(0f, (availWidth - textSize.X) / 2f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

        ImGui.PushStyleColor(ImGuiCol.Text, ColorExtensions.CameraColor(slot.Camera));
        ImGui.TextUnformatted(slot.Label);
        ImGui.PopStyleColor();
    }

    static bool RenderCameraImage(ImageSlot slot, HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
    {
        if (slot.TextureId == 0 || slot.TextureWidth <= 0 || slot.TextureHeight <= 0)
        {
            ImGui.TextUnformatted("(no image)");
            return false;
        }

        var changed = false;
        var boundsWidth = (float)slot.TextureWidth;
        var boundsHeight = (float)slot.TextureHeight;
        var avail = ImGui.GetContentRegionAvail();
        // Fit inside BOTH available dimensions (letterbox), not just width - otherwise the
        // image can overflow past the bottom of a height-constrained container.
        var scaleByWidth = avail.X / boundsWidth;
        var scaleByHeight = avail.Y > 0f ? avail.Y / boundsHeight : scaleByWidth;
        var scale = Math.Min(scaleByWidth, scaleByHeight);
        var displaySize = new Vector2(boundsWidth * scale, boundsHeight * scale);
        var origin = ImGui.GetCursorScreenPos();

        ImGui.Image(slot.TexRef, displaySize);
        var afterImage = ImGui.GetCursorScreenPos();

        var drawList = ImGui.GetWindowDrawList();
        changed |= RenderCircleOverlay(slot.Circles.Background, "B", "bg", "bg_" + slot.Camera, origin, scale, boundsWidth, boundsHeight, drawList, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
        for (int i = 0; i < slot.Circles.Rois.Count; i++)
        {
            var key = "roi" + i;
            changed |= RenderCircleOverlay(slot.Circles.Rois[i], i.ToString(), key, key + "_" + slot.Camera, origin, scale, boundsWidth, boundsHeight, drawList, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
        }

        // A bare SetCursorScreenPos with no item submitted afterward can't grow the
        // table/window's tracked content bounds; a zero-size Dummy() registers it.
        ImGui.SetCursorScreenPos(afterImage);
        ImGui.Dummy(Vector2.Zero);
        return changed;
    }

    // Circle center/radius are stored in image-pixel space; `scale` converts to/from the
    // on-screen display size. Drag moves the circle, mouse wheel while hovering resizes it.
    // `activeKey` is shared across columns (e.g. "roi0" for both the Iso and Green renditions
    // of the same yoked circle) so hovering/dragging it in any column highlights it everywhere;
    // `widgetIdSuffix` stays unique per column to avoid ImGui ID collisions.
    static unsafe bool RenderCircleOverlay(
        PickerCircle circle, string label, string activeKey, string widgetIdSuffix,
        Vector2 origin, float scale, float boundsWidth, float boundsHeight, ImDrawListPtr drawList,
        HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = ClampToBounds(circle, boundsWidth, boundsHeight);

        var isActive = previousActiveKeys.Contains(activeKey) || selectedKeys.Contains(activeKey);
        var color = isActive ? activeCircleColor : inactiveCircleColor;
        var center = origin + new Vector2(circle.X, circle.Y) * scale;
        var radius = circle.Radius * scale;

        var fillColor = new Vector4(color.X, color.Y, color.Z, circleFillAlpha);
        drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(fillColor));
        drawList.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(color), 0, circleStrokeThickness);
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(center - textSize * 0.5f, ImGui.ColorConvertFloat4ToU32(labelColor), label);

        ImGui.SetCursorScreenPos(center - new Vector2(radius, radius));
        ImGui.InvisibleButton("circle_" + widgetIdSuffix, new Vector2(radius * 2f, radius * 2f));

        // Hover/active tracking (and the resulting cross-column green highlight) stays live even
        // when disabled; only the actual drag/resize mutations below are gated on `enabled`.
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active) currentActiveKeys.Add(activeKey);

        if (enabled)
        {
            // Read via the native Handle pointer, not the ImGuiIOPtr.MouseDelta/MouseWheel
            // ref-returning properties: Bonsai's script-extension compiler rejects ref returns
            // (CS0570), same as the earlier ImGuiStylePtr.ItemSpacing issue.
            var io = ImGui.GetIO().Handle;
            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var delta = io->MouseDelta;
                if (delta.X != 0f || delta.Y != 0f)
                {
                    circle.X += delta.X / scale;
                    circle.Y += delta.Y / scale;
                    changed = true;
                }
            }

            if (hovered)
            {
                var wheel = io->MouseWheel;
                if (wheel != 0f)
                {
                    circle.Radius = Math.Max(minRadius, circle.Radius + wheel * radiusStep);
                    changed = true;
                }
            }
        }

        changed |= ClampToBounds(circle, boundsWidth, boundsHeight);
        return changed;
    }

    // One row per Roi (plus Background) for this camera. Iso and Green render separate tables
    // but both mutate the SAME shared RoiCircleSet.Rois list, so adding/deleting from either one
    // is automatically reflected in the other; Red's list is independent. Selecting a row reuses
    // the same activeKey highlighting as hover/drag, so it stays green in every column.
    static bool RenderRoiTable(ImageSlot slot, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = false;
        var rois = slot.Circles.Rois;

        ImGui.BeginDisabled(!enabled);
        if (ImGui.Button("Add ROI##" + slot.Camera, new Vector2(-1f, 0f)))
        {
            var x = slot.TextureWidth > 0 ? slot.TextureWidth / 2f : 100f;
            var y = slot.TextureHeight > 0 ? slot.TextureHeight / 2f : 100f;
            rois.Add(MakeCircle(x, y, 20));
            changed = true;
        }
        ImGui.EndDisabled();

        if (ImGui.BeginTable("##RoiTable" + slot.Camera, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            var indexWidth = ImGui.GetContentRegionAvail().X / 9f;
            ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, indexWidth);
            ImGui.TableSetupColumn("Roi", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, indexWidth);
            ImGui.TableHeadersRow();

            RenderRoiRow(slot, "bg", RoiIndexLabel(-1), RoiName(-1), selectedKeys, deletable: false, enabled: enabled);

            var deleteIndex = -1;
            for (int i = 0; i < rois.Count; i++)
            {
                if (RenderRoiRow(slot, "roi" + i, RoiIndexLabel(i), RoiName(i), selectedKeys, deletable: true, enabled: enabled)) deleteIndex = i;
            }

            if (deleteIndex >= 0)
            {
                rois.RemoveAt(deleteIndex);
                // Keys are positional ("roi2" means "index 2 in Rois"); after a delete every
                // later index shifts, so clear rather than risk a selection silently pointing
                // at a different, renumbered Roi.
                selectedKeys.Clear();
                changed = true;
            }

            ImGui.EndTable();
        }

        return changed;
    }

    static bool RenderRoiRow(ImageSlot slot, string key, string indexLabel, string name, HashSet<string> selectedKeys, bool deletable, bool enabled)
    {
        var requestDelete = false;
        var idSuffix = slot.Camera + "_" + key;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(indexLabel);

        ImGui.TableNextColumn();
        var selected = selectedKeys.Contains(key);
        // AllowOverlap: lets the Delete button (next column) sit on top of this selectable's
        // SpanAllColumns hit region and reliably take the click - without it, clicks between
        // the two resolve erratically frame to frame.
        if (ImGui.Selectable(name + "##roiRow_" + idSuffix, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
        {
            if (selected)
            {
                selectedKeys.Remove(key);
            }
            else
            {
                // Single-select: picking a new row replaces whatever was selected before, so
                // only the most recently selected Roi is ever highlighted in the images.
                selectedKeys.Clear();
                selectedKeys.Add(key);
            }
        }

        ImGui.TableNextColumn();
        if (deletable)
        {
            ImGui.BeginDisabled(!enabled);
            if (ImGui.Button("x##roiDelete_" + idSuffix)) requestDelete = true;
            ImGui.EndDisabled();
        }

        return requestDelete;
    }

    // Keeps the circle fully inside [0, boundsWidth] x [0, boundsHeight] - shrinking the radius
    // if it can't possibly fit, then repositioning the center - so a circle loaded from a stale
    // file (or dragged/resized past the edge) always snaps back inside the image.
    static bool ClampToBounds(PickerCircle circle, float boundsWidth, float boundsHeight)
    {
        if (boundsWidth <= 0f || boundsHeight <= 0f) return false;

        var beforeX = circle.X;
        var beforeY = circle.Y;
        var beforeRadius = circle.Radius;

        var maxRadius = Math.Max(minRadius, Math.Min(boundsWidth, boundsHeight) / 2f);
        circle.Radius = Math.Min(Math.Max(circle.Radius, minRadius), maxRadius);
        circle.X = ClampValue(circle.X, circle.Radius, boundsWidth - circle.Radius);
        circle.Y = ClampValue(circle.Y, circle.Radius, boundsHeight - circle.Radius);

        return circle.X != beforeX || circle.Y != beforeY || circle.Radius != beforeRadius;
    }

    static float ClampValue(float value, float min, float max)
    {
        if (max < min) return (min + max) / 2f;
        return Math.Min(Math.Max(value, min), max);
    }

    static unsafe void UpdateTexture(ImageSlot slot, object imageLock)
    {
        IplImage image;
        bool dirty;
        lock (imageLock)
        {
            image = slot.PendingImage;
            dirty = slot.ImageDirty;
            slot.ImageDirty = false;
        }
        if (!dirty || image == null) return;

        // Raw FIP frames are often >8-bit (e.g. U16) with the real signal occupying only a
        // small slice of that range, so uploading as-is (GL normalizes against the full
        // 16-bit range) looks almost black. Auto-stretch to 0-255 for this preview texture
        // only - never touches the actual photometry signal.
        var display = image.Depth == IplDepth.U8 ? image : NormalizeForDisplay(image);
        try
        {
            var sizeChanged = display.Width != slot.TextureWidth || display.Height != slot.TextureHeight;
            if (slot.TextureId == 0)
            {
                int textureId;
                GL.GenTextures(1, out textureId);
                slot.TextureId = textureId;
                slot.TexRef = new ImTextureRef(texId: new ImTextureID(unchecked((ulong)(uint)textureId)));
                GL.BindTexture(TextureTarget.Texture2D, textureId);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

                // GL_LUMINANCE (single-channel upload) was removed in core-profile OpenGL;
                // GL_RED + this swizzle (R/G/B -> Red, A -> One) reproduces the same look.
                if (display.Channels == 1)
                {
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleR, (int)All.Red);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int)All.Red);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int)All.Red);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA, (int)All.One);
                }
                else
                {
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleR, (int)All.Red);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleG, (int)All.Green);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleB, (int)All.Blue);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureSwizzleA, (int)All.Alpha);
                }

                sizeChanged = true;
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, slot.TextureId);
            }

            UploadPixels(display, sizeChanged);
            slot.TextureWidth = display.Width;
            slot.TextureHeight = display.Height;
        }
        finally
        {
            if (display != image) display.Dispose();
        }
    }

    static IplImage NormalizeForDisplay(IplImage source)
    {
        var display = new IplImage(source.Size, IplDepth.U8, source.Channels);
        CV.Normalize(source, display, 0, 255, NormTypes.MinMax, null);
        return display;
    }

    static void UploadPixels(IplImage image, bool allocate)
    {
        PixelFormat pixelFormat;
        int pixelSize;
        PixelType pixelType;
        GetPixelFormat(image, out pixelFormat, out pixelSize, out pixelType);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, image.WidthStep % 4 == 0 ? 4 : 1);
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, image.WidthStep / (pixelSize * image.Channels));
        if (allocate)
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, pixelFormat, pixelType, image.ImageData);
        }
        else
        {
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, image.Width, image.Height, pixelFormat, pixelType, image.ImageData);
        }
        GC.KeepAlive(image);
    }

    static void GetPixelFormat(IplImage image, out PixelFormat pixelFormat, out int pixelSize, out PixelType pixelType)
    {
        switch (image.Channels)
        {
            // Paired with the swizzle set up in UpdateTexture (GL_RED replaces GL_LUMINANCE).
            case 1: pixelFormat = PixelFormat.Red; break;
            case 2: pixelFormat = PixelFormat.Rg; break;
            case 3: pixelFormat = PixelFormat.Bgr; break;
            case 4: pixelFormat = PixelFormat.Bgra; break;
            default: throw new ArgumentException("Image has an unsupported number of channels.");
        }

        switch (image.Depth)
        {
            case IplDepth.U8: pixelSize = 1; pixelType = PixelType.UnsignedByte; break;
            case IplDepth.S8: pixelSize = 1; pixelType = PixelType.Byte; break;
            case IplDepth.U16: pixelSize = 2; pixelType = PixelType.UnsignedShort; break;
            case IplDepth.S16: pixelSize = 2; pixelType = PixelType.Short; break;
            case IplDepth.S32: pixelSize = 4; pixelType = PixelType.Int; break;
            case IplDepth.F32: pixelSize = 4; pixelType = PixelType.Float; break;
            default: throw new ArgumentException("Image has an unsupported pixel bit depth.");
        }
    }
}

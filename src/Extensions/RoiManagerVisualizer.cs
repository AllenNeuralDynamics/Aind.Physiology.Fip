using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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
/// ("yoked"). Red has its own circle positions, but the same set of ROIs: a single table below
/// the images lists every ROI once (ROI number = row index) with its fiber number, name and
/// enabled flag, which apply to all cameras. Add ROI adds a circle to every camera; deleting a
/// row removes it from every camera. Emits the current <see cref="AindPhysiologyFip.RoiSettings"/>
/// whenever anything changes. "Save Roi"/"Load Roi"/"Reset Roi" each require confirming an
/// inline popup; Save/Load only persist circle positions (not fiber/name/enabled), to/from
/// <see cref="LocalRoiDefaultPath"/>. <see cref="RoiSettings"/> (the property) is only consulted
/// once, to seed the initial ROIs (positions and metadata) if set.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders a live-image ROI picker with one column per camera (Iso, Green, Red; Iso/Green share yoked ROIs) and a single ROI table shared by all cameras, plus Save Roi/Load Roi/Reset Roi (each behind a confirm popup) and Add ROI buttons, emitting the current RoiSettings whenever it changes. Source1 = Frame subject; Source2 = the grouped Fip activity stream (e.g. ParsedFipStreams), used only for the live preview images.")]
public class RoiManagerVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private bool enabled = true;
    [Description("Specifies whether Save/Load/Reset/Add, the ROI table and circle drag/resize are enabled. When false, everything is read-only, but circles still highlight green on hover.")]
    public bool Enabled { get { return enabled; } set { enabled = value; } }

    private float fontSize = 20f;
    [Description("Font size used to render the control.")]
    public float FontSize { get { return fontSize; } set { fontSize = value; } }

    private string localRoiDefaultPath = "../local/default.json";
    [Description("The path used by Save Roi/Load Roi to persist the ROI circle positions.")]
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
    static readonly Vector4 activeCircleColor = new Vector4(0.15f, 0.85f, 0.15f, 1f);
    static readonly Vector4 labelColor = new Vector4(1f, 1f, 1f, 1f);

    static readonly Vector4 disabledCircleColor = new Vector4(0.55f, 0.55f, 0.55f, 1f);

    const int defaultRoiCount = 4;
    const float defaultRadius = 20f;
    const float controlButtonWidth = 140f;

    // Geometry of one circle, in that camera's image-pixel space.
    class PickerCircle
    {
        public float X;
        public float Y;
        public float Radius;
    }

    // Per-Roi metadata shared by every camera: Roi i is the same fiber in Green/Iso and in Red,
    // so its fiber number, name and enabled flag are edited once and written to both camera
    // lists. The RoiNumber is always the row index.
    class RoiMeta
    {
        public int FiberNumber;
        public string Name;
        public bool Enabled = true;
    }

    class RoiCircleSet
    {
        public PickerCircle Background;
        public List<PickerCircle> Rois;
    }

    // Invariant: GreenIso.Rois, Red.Rois and Meta always have the same length (row i = Roi i in
    // every camera). The objects themselves are never replaced, only mutated, so ImageSlots can
    // keep references to them.
    class RoiState
    {
        public RoiCircleSet GreenIso;
        public RoiCircleSet Red;
        public List<RoiMeta> Meta;
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

    static PickerCircle DefaultRoiCircle(int index)
    {
        // 2x2 grid for the first four, then stacked at the last grid position.
        var offsets = new float[] { 50, 150 };
        var i = Math.Min(index, defaultRoiCount - 1);
        return MakeCircle(offsets[i / 2], offsets[i % 2], defaultRadius);
    }

    static PickerCircle DefaultBackground()
    {
        return MakeCircle(0, 0, defaultRadius);
    }

    static void ResetToDefault(RoiState state)
    {
        foreach (var set in new[] { state.GreenIso, state.Red })
        {
            set.Background = DefaultBackground();
            set.Rois = Enumerable.Range(0, defaultRoiCount).Select(DefaultRoiCircle).ToList();
        }
        state.Meta.Clear();
        ResizeMeta(state.Meta, defaultRoiCount);
    }

    static RoiState MakeDefaultState()
    {
        var state = new RoiState
        {
            GreenIso = new RoiCircleSet(),
            Red = new RoiCircleSet(),
            Meta = new List<RoiMeta>(),
        };
        ResetToDefault(state);
        return state;
    }

    // Smallest non-negative fiber number not already used, so adding an Roi fills gaps left by
    // deletions before growing the range.
    static int NextFreeFiber(List<RoiMeta> meta)
    {
        var used = new HashSet<int>(meta.Select(m => m.FiberNumber));
        var next = 0;
        while (used.Contains(next)) next++;
        return next;
    }

    // Sets `target`'s fiber number to `value`; if another Roi already has `value`, it takes
    // `target`'s previous number instead (a swap), so fiber numbers stay unique.
    static bool AssignUniqueFiber(List<RoiMeta> meta, RoiMeta target, int value)
    {
        var previous = target.FiberNumber;
        if (previous == value) return false;
        foreach (var other in meta)
        {
            if (other != target && other.FiberNumber == value) other.FiberNumber = previous;
        }
        target.FiberNumber = value;
        return true;
    }

    // Drops or appends Rois so there are exactly `count`; new ones get the next free fiber.
    static void ResizeMeta(List<RoiMeta> meta, int count)
    {
        while (meta.Count > count) meta.RemoveAt(meta.Count - 1);
        while (meta.Count < count) meta.Add(new RoiMeta { FiberNumber = NextFreeFiber(meta) });
    }

    static FipCircle ElementAtOrNull(List<FipCircle> list, int index)
    {
        return list != null && index < list.Count ? list[index] : null;
    }

    static int RoiCount(RoiSettings settings)
    {
        var greenIso = settings.CameraGreenIsoRoi == null ? 0 : settings.CameraGreenIsoRoi.Count;
        var red = settings.CameraRedRoi == null ? 0 : settings.CameraRedRoi.Count;
        return Math.Max(greenIso, red);
    }

    static PickerCircle FromFipCircle(FipCircle c, PickerCircle fallback)
    {
        if (c == null || c.Center == null) return fallback;
        return MakeCircle((float)c.Center.X, (float)c.Center.Y, (float)c.Radius);
    }

    // Replaces both cameras' circle positions with the ones in `settings`. The Roi count becomes
    // the larger of the two lists; a camera that's missing a circle gets a default one. Only
    // geometry is read here - callers decide what happens to the metadata. Returns the count.
    static int ApplyGeometry(RoiState state, RoiSettings settings)
    {
        var count = RoiCount(settings);
        if (count == 0) count = defaultRoiCount;

        state.GreenIso.Background = FromFipCircle(settings.CameraGreenIsoBackground, DefaultBackground());
        state.Red.Background = FromFipCircle(settings.CameraRedBackground, DefaultBackground());
        state.GreenIso.Rois = Enumerable.Range(0, count)
            .Select(i => FromFipCircle(ElementAtOrNull(settings.CameraGreenIsoRoi, i), DefaultRoiCircle(i)))
            .ToList();
        state.Red.Rois = Enumerable.Range(0, count)
            .Select(i => FromFipCircle(ElementAtOrNull(settings.CameraRedRoi, i), DefaultRoiCircle(i)))
            .ToList();
        return count;
    }

    // Seeding (from the RoiSettings property) takes geometry AND metadata. Metadata is read from
    // the Green/Iso list, falling back to Red; missing or duplicate fiber numbers are replaced
    // with the next free one.
    static void ApplySeed(RoiState state, RoiSettings seed)
    {
        var count = ApplyGeometry(state, seed);
        state.Meta.Clear();
        var used = new HashSet<int>();
        var needsFiber = new List<RoiMeta>();
        for (int i = 0; i < count; i++)
        {
            var source = ElementAtOrNull(seed.CameraGreenIsoRoi, i) ?? ElementAtOrNull(seed.CameraRedRoi, i);
            var meta = new RoiMeta();
            if (source != null)
            {
                meta.Name = source.Name;
                meta.Enabled = source.Enabled;
            }
            if (source != null && source.FiberNumber.HasValue && source.FiberNumber.Value >= 0 && used.Add(source.FiberNumber.Value))
            {
                meta.FiberNumber = source.FiberNumber.Value;
            }
            else
            {
                meta.FiberNumber = -1;
                needsFiber.Add(meta);
            }
            state.Meta.Add(meta);
        }
        foreach (var meta in needsFiber) meta.FiberNumber = NextFreeFiber(state.Meta);
    }

    static FipCircle ToGeometryCircle(PickerCircle c)
    {
        return new FipCircle { Center = new AindPhysiologyFip.Point2f { X = c.X, Y = c.Y }, Radius = c.Radius };
    }

    static FipCircle ToRoiCircle(PickerCircle c, RoiMeta meta, int index)
    {
        var circle = ToGeometryCircle(c);
        circle.RoiNumber = index;
        circle.FiberNumber = meta.FiberNumber;
        circle.Name = meta.Name;
        circle.Enabled = meta.Enabled;
        return circle;
    }

    static List<FipCircle> ToRoiCircles(RoiCircleSet set, List<RoiMeta> meta)
    {
        return Enumerable.Range(0, meta.Count).Select(i => ToRoiCircle(set.Rois[i], meta[i], i)).ToList();
    }

    static RoiSettings ToRoiSettings(RoiState state)
    {
        return new RoiSettings
        {
            CameraGreenIsoBackground = ToGeometryCircle(state.GreenIso.Background),
            CameraGreenIsoRoi = ToRoiCircles(state.GreenIso, state.Meta),
            CameraRedBackground = ToGeometryCircle(state.Red.Background),
            CameraRedRoi = ToRoiCircles(state.Red, state.Meta),
        };
    }

    // What Save Roi writes: circle positions only, no fiber/name/enabled assignments.
    static RoiSettings ToGeometrySettings(RoiState state)
    {
        return new RoiSettings
        {
            CameraGreenIsoBackground = ToGeometryCircle(state.GreenIso.Background),
            CameraGreenIsoRoi = state.GreenIso.Rois.Select(ToGeometryCircle).ToList(),
            CameraRedBackground = ToGeometryCircle(state.Red.Background),
            CameraRedRoi = state.Red.Rois.Select(ToGeometryCircle).ToList(),
        };
    }

    static PickerCircle ImageCenterCircle(List<ImageSlot> slots, Func<ImageSlot, bool> predicate)
    {
        var slot = slots.FirstOrDefault(s => predicate(s) && s.TextureWidth > 0 && s.TextureHeight > 0);
        if (slot == null) return MakeCircle(100f, 100f, defaultRadius);
        return MakeCircle(slot.TextureWidth / 2f, slot.TextureHeight / 2f, defaultRadius);
    }

    static void AddRoi(RoiState state, List<ImageSlot> slots)
    {
        state.GreenIso.Rois.Add(ImageCenterCircle(slots, s => s.Camera != FipCameraSource.Red));
        state.Red.Rois.Add(ImageCenterCircle(slots, s => s.Camera == FipCameraSource.Red));
        state.Meta.Add(new RoiMeta { FiberNumber = NextFreeFiber(state.Meta) });
    }

    static void RemoveRoi(RoiState state, int index)
    {
        state.GreenIso.Rois.RemoveAt(index);
        state.Red.Rois.RemoveAt(index);
        state.Meta.RemoveAt(index);
    }

    public IObservable<RoiSettings> Process<TTickSource>(
        IObservable<TTickSource> frames,
        IObservable<System.Reactive.Linq.IGroupedObservable<FipCameraSource, Bonsai.Harp.Timestamped<CircleActivityCollection>>> streams)
    {
        return Observable.Create<RoiSettings>(observer =>
        {
            var state = MakeDefaultState();
            var slots = new List<ImageSlot>();
            for (int i = 0; i < cameraOrder.Length; i++)
            {
                var camera = cameraOrder[i];
                slots.Add(new ImageSlot
                {
                    Camera = camera,
                    Label = ColorExtensions.CameraDisplayName(camera),
                    Circles = camera == FipCameraSource.Red ? state.Red : state.GreenIso,
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
                        if (seed != null) ApplySeed(state, seed);
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

                    var currentActiveKeys = new HashSet<string>();
                    changed |= RenderCameras(slots, state.Meta, previousActiveKeys, currentActiveKeys, selectedKeys, Enabled);
                    previousActiveKeys = currentActiveKeys;

                    ImGui.TextDisabled("Drag a circle to move it  -  Scroll while hovering it to resize");

                    // Buttons stacked in a fixed-width column to the left of the Roi table.
                    if (ImGui.BeginTable("##RoiManagerBottom", 2, ImGuiTableFlags.None))
                    {
                        ImGui.TableSetupColumn("##RoiManagerControls", ImGuiTableColumnFlags.WidthFixed, controlButtonWidth);
                        ImGui.TableSetupColumn("##RoiManagerTable", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.BeginDisabled(!Enabled);
                        RenderControls(state, slots, selectedKeys, ref changed);
                        ImGui.EndDisabled();

                        ImGui.TableNextColumn();
                        changed |= RenderRoiTable(state, selectedKeys, Enabled);

                        ImGui.EndTable();
                    }

                    ImGui.EndChild();

                    ImGui.PopFont();

                    if (changed) observer.OnNext(ToRoiSettings(state));
                },
                observer.OnError,
                observer.OnCompleted));

            return new CompositeDisposable(streamsSub, innerSubs, frameSub);
        });
    }

    void RenderControls(RoiState state, List<ImageSlot> slots, HashSet<string> selectedKeys, ref bool changed)
    {
        var buttonSize = new Vector2(controlButtonWidth, ImGui.GetFrameHeight() * 1.6f);

        if (ImGui.Button("Save Roi##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmSaveRoi##RoiManager");
        if (ConfirmPopup("ConfirmSaveRoi##RoiManager", "Overwrite the saved ROI positions on disk?"))
        {
            SaveToDisk(state);
        }

        if (ImGui.Button("Load Roi##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmLoadRoi##RoiManager");
        if (ConfirmPopup("ConfirmLoadRoi##RoiManager", "Load the saved ROI positions? This overwrites the current positions."))
        {
            if (LoadFromDisk(state))
            {
                selectedKeys.Clear();
                changed = true;
            }
        }

        if (ImGui.Button("Reset Roi##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmResetRoi##RoiManager");
        if (ConfirmPopup("ConfirmResetRoi##RoiManager", "Reset to the default ROI configuration?"))
        {
            ResetToDefault(state);
            selectedKeys.Clear();
            changed = true;
        }

        if (ImGui.Button("Add ROI##RoiManager", buttonSize))
        {
            AddRoi(state, slots);
            changed = true;
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

    void SaveToDisk(RoiState state)
    {
        var json = JsonConvert.SerializeObject(ToGeometrySettings(state));
        var directory = Path.GetDirectoryName(LocalRoiDefaultPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(LocalRoiDefaultPath, json);
    }

    // Loads circle positions only. Fiber/name/enabled stay as they are for the Rois that still
    // exist; if the file has more Rois, the extra ones get default metadata.
    bool LoadFromDisk(RoiState state)
    {
        if (!File.Exists(LocalRoiDefaultPath)) return false;
        var json = File.ReadAllText(LocalRoiDefaultPath);
        var settings = JsonConvert.DeserializeObject<RoiSettings>(json);
        if (settings == null) return false;
        var count = ApplyGeometry(state, settings);
        ResizeMeta(state.Meta, count);
        return true;
    }

    static bool RenderCameras(List<ImageSlot> slots, List<RoiMeta> meta, HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
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
                changed |= RenderCameraImage(slot, meta, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
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

    static bool RenderCameraImage(ImageSlot slot, List<RoiMeta> meta, HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
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
        changed |= RenderCircleOverlay(slot.Circles.Background, "B", null, true, ColorExtensions.BackgroundColor, "bg", "bg_" + slot.Camera, origin, scale, boundsWidth, boundsHeight, drawList, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
        for (int i = 0; i < slot.Circles.Rois.Count; i++)
        {
            var key = "roi" + i;
            changed |= RenderCircleOverlay(slot.Circles.Rois[i], i.ToString(), meta[i].Name, meta[i].Enabled, ColorExtensions.RoiColor(i), key, key + "_" + slot.Camera, origin, scale, boundsWidth, boundsHeight, drawList, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
        }

        // A bare SetCursorScreenPos with no item submitted afterward can't grow the
        // table/window's tracked content bounds; a zero-size Dummy() registers it.
        ImGui.SetCursorScreenPos(afterImage);
        ImGui.Dummy(Vector2.Zero);
        return changed;
    }

    // Circle center/radius are stored in image-pixel space; `scale` converts to/from the
    // on-screen display size. Drag moves the circle, mouse wheel while hovering resizes it.
    // `activeKey` is shared across columns (e.g. "roi0" for every camera's rendition of Roi 0)
    // so hovering/dragging it in any column highlights it everywhere; `widgetIdSuffix` stays
    // unique per column to avoid ImGui ID collisions.
    static unsafe bool RenderCircleOverlay(
        PickerCircle circle, string label, string name, bool roiEnabled, Vector4 roiColor, string activeKey, string widgetIdSuffix,
        Vector2 origin, float scale, float boundsWidth, float boundsHeight, ImDrawListPtr drawList,
        HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = ClampToBounds(circle, boundsWidth, boundsHeight);

        var isActive = previousActiveKeys.Contains(activeKey) || selectedKeys.Contains(activeKey);
        // Each Roi has its own fixed color (see ColorExtensions.RoiColor); hover/drag or table
        // selection turns it green. Disabled Rois are drawn grey (still draggable/selectable) so they read as "excluded".
        var color = isActive ? activeCircleColor : roiEnabled ? roiColor : disabledCircleColor;
        var center = origin + new Vector2(circle.X, circle.Y) * scale;
        var radius = circle.Radius * scale;

        var fillColor = new Vector4(color.X, color.Y, color.Z, circleFillAlpha);
        drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(fillColor));
        drawList.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(color), 0, circleStrokeThickness);
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(center - textSize * 0.5f, ImGui.ColorConvertFloat4ToU32(labelColor), label);
        if (!string.IsNullOrEmpty(name))
        {
            var nameSize = ImGui.CalcTextSize(name);
            drawList.AddText(center + new Vector2(-nameSize.X * 0.5f, radius + 2f), ImGui.ColorConvertFloat4ToU32(color), name);
        }

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

    // One table for every camera: row i is Roi i in Green/Iso and in Red, so editing a row or
    // deleting it applies to all cameras. Selecting a row reuses the same activeKey highlighting
    // as hover/drag, so that Roi stays green in every column.
    static bool RenderRoiTable(RoiState state, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = false;

        // Columns mirror AindPhysiologyFip.FipCircle: RoiNumber (the row index), FiberNumber,
        // Name, Enabled.
        if (ImGui.BeginTable("##RoiTable", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            var roiWidth = ImGui.CalcTextSize("ROI").X + ImGui.GetFrameHeight() * 0.5f;
            // Room for a 3-digit number plus the two arrow buttons.
            var fiberWidth = ImGui.CalcTextSize("000").X + ImGui.GetFrameHeight() * 2.5f;
            ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, roiWidth);
            ImGui.TableSetupColumn("Fiber", ImGuiTableColumnFlags.WidthFixed, fiberWidth);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            RenderBackgroundRow(state, selectedKeys);

            var deleteIndex = -1;
            for (int i = 0; i < state.Meta.Count; i++)
            {
                if (RenderRoiRow(state, i, selectedKeys, enabled, ref changed)) deleteIndex = i;
            }

            if (deleteIndex >= 0)
            {
                RemoveRoi(state, deleteIndex);
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

    static string DescribeCircle(string camera, PickerCircle circle)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "{0}: center ({1:F1}, {2:F1}) px, radius {3:F1} px", camera, circle.X, circle.Y, circle.Radius);
    }

    // Row-wide selectable drawn first in the row, with an empty label so the cell's own widgets
    // render on top of it. AllowOverlap lets those widgets (inputs, checkbox, delete button)
    // reliably take their clicks over the selectable's SpanAllColumns hit region. Hovering the
    // row outside those widgets shows the circle's geometry in every camera as a tooltip.
    static void RenderRowSelectable(string key, PickerCircle greenIso, PickerCircle red, HashSet<string> selectedKeys)
    {
        var selected = selectedKeys.Contains(key);
        if (ImGui.Selectable("##roiRow_" + key, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap, new Vector2(0f, ImGui.GetFrameHeight())))
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
        if (ImGui.IsItemHovered() && ImGui.BeginTooltip())
        {
            ImGui.TextUnformatted(DescribeCircle("Green/Iso", greenIso) + "\n" + DescribeCircle("Red", red));
            ImGui.EndTooltip();
        }
        ImGui.SameLine(0f, 0f);
    }

    // Only the Background circles' geometry is used, so their row shows fixed placeholders
    // instead of editable Roi metadata.
    static void RenderBackgroundRow(RoiState state, HashSet<string> selectedKeys)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        RenderRowSelectable("bg", state.GreenIso.Background, state.Red.Background, selectedKeys);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColorExtensions.BackgroundColor, "B");

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("-");

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Background");

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
    }

    static bool RenderRoiRow(RoiState state, int index, HashSet<string> selectedKeys, bool enabled, ref bool changed)
    {
        var requestDelete = false;
        var key = "roi" + index;
        var meta = state.Meta[index];

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        RenderRowSelectable(key, state.GreenIso.Rois[index], state.Red.Rois[index], selectedKeys);

        // Disabled Rois render their text grey, matching their grey circle in the images; the
        // Roi number is otherwise shown in that Roi's circle color.
        if (!meta.Enabled) ImGui.PushStyleColor(ImGuiCol.Text, disabledCircleColor);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(meta.Enabled ? ColorExtensions.RoiColor(index) : disabledCircleColor, index.ToString(CultureInfo.InvariantCulture));

        ImGui.BeginDisabled(!enabled);
        ImGui.TableNextColumn();
        changed |= UniqueFiberInput("##fiberNumber_" + key, state.Meta, meta);

        ImGui.TableNextColumn();
        var name = meta.Name ?? string.Empty;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##roiName_" + key, "(unnamed)", ref name, new UIntPtr(128u)))
        {
            meta.Name = name.Length == 0 ? null : name;
            changed = true;
        }
        if (!meta.Enabled) ImGui.PopStyleColor();

        ImGui.TableNextColumn();
        if (ImGui.Checkbox("##roiEnabled_" + key, ref meta.Enabled)) changed = true;

        ImGui.TableNextColumn();
        if (ImGui.Button("x##roiDelete_" + key)) requestDelete = true;
        ImGui.EndDisabled();

        return requestDelete;
    }

    // The number field currently being typed into, and its uncommitted text. Typed numbers are
    // only applied once the field is left (Enter or click away; Escape cancels), so typing "12"
    // doesn't swap through fiber 1 on the way.
    static string pendingNumberId;
    static string pendingNumberText;

    // Non-negative fiber number field with up/down arrow buttons. Picking a fiber number another
    // Roi already uses swaps the two (see AssignUniqueFiber).
    static bool UniqueFiberInput(string id, List<RoiMeta> allMeta, RoiMeta meta)
    {
        var changed = false;
        var value = meta.FiberNumber;
        var arrowWidth = ImGui.GetFrameHeight();

        var text = pendingNumberId == id ? pendingNumberText : value.ToString(CultureInfo.InvariantCulture);
        ImGui.SetNextItemWidth(Math.Max(1f, ImGui.GetContentRegionAvail().X - 2f * arrowWidth));
        if (ImGui.InputText(id, ref text, new UIntPtr(16u), ImGuiInputTextFlags.CharsDecimal))
        {
            pendingNumberId = id;
            pendingNumberText = text;
        }
        if (ImGui.IsItemDeactivated() && pendingNumberId == id)
        {
            int parsed;
            if (!ImGui.IsKeyPressed(ImGuiKey.Escape) &&
                int.TryParse(pendingNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
            {
                changed |= AssignUniqueFiber(allMeta, meta, parsed);
            }
            pendingNumberId = null;
            pendingNumberText = null;
        }

        ImGui.SameLine(0f, 0f);
        if (ImGui.ArrowButton(id + "_up", ImGuiDir.Up)) changed |= AssignUniqueFiber(allMeta, meta, value + 1);
        ImGui.SameLine(0f, 0f);
        ImGui.BeginDisabled(value <= 0);
        if (ImGui.ArrowButton(id + "_down", ImGuiDir.Down)) changed |= AssignUniqueFiber(allMeta, meta, value - 1);
        ImGui.EndDisabled();
        return changed;
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

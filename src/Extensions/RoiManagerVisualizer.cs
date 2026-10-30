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
/// whenever anything changes. "Save ROI"/"Load ROI"/"Reset ROI" each require confirming an
/// inline popup; Save/Load only persist circle positions (not fiber/name/enabled), to/from
/// <see cref="LocalRoiDefaultPath"/>. At startup the ROI positions are loaded from
/// <see cref="LocalRoiDefaultPath"/> if it is set; if that fails, a warning is shown and the
/// Reset ROI defaults are used.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders a live-image ROI picker with one column per camera (Iso, Green, Red; Iso/Green share yoked ROIs) and a single ROI table shared by all cameras, plus Save ROI/Load ROI/Reset ROI (each behind a confirm popup) and Add ROI buttons, emitting the current RoiSettings whenever it changes. Source1 = Frame subject; Source2 = the grouped Fip activity stream (e.g. ParsedFipStreams), used only for the live preview images.")]
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
    [Description("The path used by Save ROI/Load ROI to persist the ROI circle positions. If set, the ROI positions are also loaded from it at startup (falling back to the defaults, with a warning, if that fails).")]
    [FileNameFilter("JSON|*.json|All Files|*.*")]
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    public string LocalRoiDefaultPath { get { return localRoiDefaultPath; } set { localRoiDefaultPath = value; } }

    static readonly FipCameraSource[] cameraOrder = { FipCameraSource.Iso, FipCameraSource.Green, FipCameraSource.Red };

    const float minRadius = 2f;
    const float radiusStep = 2f;
    const float circleFillAlpha = 0.35f;
    const float circleStrokeThickness = 4f;
    const float swatchBorderThickness = 3f;
    static readonly Vector4 activeCircleColor = new Vector4(0.15f, 0.85f, 0.15f, 1f);
    static readonly Vector4 labelColor = new Vector4(1f, 1f, 1f, 1f);

    static readonly Vector4 disabledCircleColor = new Vector4(0.55f, 0.55f, 0.55f, 1f);

    const int defaultRoiCount = 4;
    const float defaultRadius = 20f;
    const float controlButtonWidth = 140f;
    const float cameraBorderThickness = 4f;

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
    // `target`'s previous number instead (a swap), so fiber numbers stay unique. Name and enabled
    // belong to the fiber, so they travel with the fiber number in a swap.
    static bool AssignUniqueFiber(List<RoiMeta> meta, RoiMeta target, int value)
    {
        var previous = target.FiberNumber;
        if (previous == value) return false;
        foreach (var other in meta)
        {
            if (other == target || other.FiberNumber != value) continue;
            other.FiberNumber = previous;
            var name = other.Name;
            other.Name = target.Name;
            target.Name = name;
            var otherEnabled = other.Enabled;
            other.Enabled = target.Enabled;
            target.Enabled = otherEnabled;
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

    // What Save ROI writes: circle positions only, no fiber/name/enabled assignments.
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
                    var editable = Enabled && !locked;
                    if (!seeded)
                    {
                        // Startup: load the saved ROI positions, falling back to the Reset ROI
                        // defaults if that fails.
                        string error;
                        if (!string.IsNullOrEmpty(LocalRoiDefaultPath) && !TryLoadFromDisk(state, out error))
                        {
                            ResetToDefault(state);
                            warningMessage = "Could not load the saved ROIs at startup:\n" + error + "\n\nUsing the default ROIs instead.";
                        }
                        seeded = true;
                        changed = true;
                    }

                    foreach (var slot in slots) UpdateTexture(slot, imageLock);

                    // If anything below throws mid-frame, the managed exception would otherwise
                    // leave ImGui's window/table/popup stacks unbalanced, and the next native
                    // ImGui call aborts the whole Bonsai process (0xc0000409). Snapshot ImGui's
                    // state first so it can be unwound, then surface the exception as a normal
                    // workflow error instead.
                    var recoveryState = new ImGuiErrorRecoveryState();
                    ImGuiP.ErrorRecoveryStoreState(ref recoveryState);
                    try
                    {
                        ImGui.PushFont(ImGui.GetFont(), FontSize);

                        // Fixed-size child + AlwaysVerticalScrollbar keep our size and scrollbar
                        // gutter stable across frames - otherwise small content-height changes
                        // (image scaling, Roi table row count) flip a scrollbar on/off, which
                        // itself changes the available width, causing visible jitter.
                        ImGui.BeginChild("##RoiManagerCanvas", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);

                        RenderWarningPopup();

                        var currentActiveKeys = new HashSet<string>();
                        changed |= RenderCameras(slots, state.Meta, previousActiveKeys, currentActiveKeys, selectedKeys, editable, !hideRois);
                        previousActiveKeys = currentActiveKeys;

                        ImGui.TextDisabled("Drag a circle to move it  -  Scroll while hovering it to resize");

                        // Buttons stacked in a fixed-width column to the left of the Roi table.
                        if (ImGui.BeginTable("##RoiManagerBottom", 2, ImGuiTableFlags.None))
                        {
                            ImGui.TableSetupColumn("##RoiManagerControls", ImGuiTableColumnFlags.WidthFixed, controlButtonWidth * 2f + 8f);
                            ImGui.TableSetupColumn("##RoiManagerTable", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            // The lock is outside any disabled block so it can always be toggled.
                            // Hide ROIs is view-only, so it stays usable during acquisition
                            // (Enabled=false), but it is still covered by the lock.
                            RenderLockButton();
                            ImGui.SameLine();
                            ImGui.BeginDisabled(locked);
                            RenderHideRoisButton();
                            ImGui.EndDisabled();
                            ImGui.BeginDisabled(!editable);
                            RenderControls(state, slots, selectedKeys, ref changed);
                            ImGui.EndDisabled();

                            ImGui.TableNextColumn();
                            changed |= RenderRoiTable(state, selectedKeys, editable);

                            ImGui.EndTable();
                        }

                        ImGui.EndChild();

                        ImGui.PopFont();
                    }
                    catch (Exception ex)
                    {
                        ImGuiP.ErrorRecoveryTryToRecoverState(ref recoveryState);
                        observer.OnError(ex);
                        return;
                    }

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

        if (ImGui.Button("Save ROI##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmSaveRoi##RoiManager");
        if (ConfirmPopup("ConfirmSaveRoi##RoiManager", "Overwrite the saved ROI positions on disk?"))
        {
            string error;
            if (!TrySaveToDisk(state, out error))
            {
                warningMessage = "Could not save the ROIs:\n" + error;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Load ROI##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmLoadRoi##RoiManager");
        if (ConfirmPopup("ConfirmLoadRoi##RoiManager", "Load the saved ROI positions? This overwrites the current positions."))
        {
            string error;
            if (TryLoadFromDisk(state, out error))
            {
                selectedKeys.Clear();
                changed = true;
            }
            else
            {
                warningMessage = "Could not load the saved ROIs:\n" + error + "\n\nThe current ROIs were kept.";
            }
        }

        if (ImGui.Button("Reset ROI##RoiManager", buttonSize)) ImGui.OpenPopup("ConfirmResetRoi##RoiManager");
        if (ConfirmPopup("ConfirmResetRoi##RoiManager", "Reset to the default ROI configuration?"))
        {
            ResetToDefault(state);
            selectedKeys.Clear();
            changed = true;
        }

        ImGui.SameLine();
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

    // Never throws: this runs mid-frame, and an exception escaping there leaves ImGui's
    // Begin/End stacks unbalanced. On failure `error` says why.
    bool TrySaveToDisk(RoiState state, out string error)
    {
        try
        {
            var json = JsonConvert.SerializeObject(ToGeometrySettings(state));
            var directory = Path.GetDirectoryName(LocalRoiDefaultPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(LocalRoiDefaultPath, json);
        }
        catch (Exception ex)
        {
            error = LocalRoiDefaultPath + ": " + ex.Message;
            return false;
        }
        error = null;
        return true;
    }

    // Loads circle positions only. Fiber/name/enabled stay as they are for the Rois that still
    // exist; if the file has more Rois, the extra ones get default metadata. On failure `state`
    // is left untouched and `error` says why.
    bool TryLoadFromDisk(RoiState state, out string error)
    {
        RoiSettings settings;
        try
        {
            if (!File.Exists(LocalRoiDefaultPath))
            {
                error = "File not found: " + Path.GetFullPath(LocalRoiDefaultPath);
                return false;
            }
            settings = JsonConvert.DeserializeObject<RoiSettings>(File.ReadAllText(LocalRoiDefaultPath));
        }
        catch (Exception ex)
        {
            error = LocalRoiDefaultPath + ": " + ex.Message;
            return false;
        }
        if (settings == null)
        {
            error = LocalRoiDefaultPath + ": the file is empty.";
            return false;
        }

        var count = ApplyGeometry(state, settings);
        ResizeMeta(state.Meta, count);
        error = null;
        return true;
    }

    // Set when a load fails (at startup or from Load ROI); shown as a modal until dismissed.
    string warningMessage;

    // "Hide ROIs" toggle: when set, the circles aren't drawn (or draggable) on the images.
    bool hideRois;
    // Locked at startup so the ROIs can't be changed by accident before the experiment.
    bool locked = true;

    static readonly Vector4 toggleOffColor = new Vector4(0.35f, 0.35f, 0.35f, 1f);
    static readonly Vector4 toggleOnColor = new Vector4(0.20f, 0.45f, 0.85f, 1f);

    // Grey while ROIs are shown, blue while they're hidden.
    void RenderLockButton()
    {
        var color = locked ? toggleOnColor : toggleOffColor;
        var hovered = new Vector4(Math.Min(1f, color.X + 0.1f), Math.Min(1f, color.Y + 0.1f), Math.Min(1f, color.Z + 0.1f), 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        if (ImGui.Button((locked ? "Locked" : "Unlocked") + "##RoiManagerLock", new Vector2(controlButtonWidth, ImGui.GetFrameHeight() * 1.6f)))
        {
            locked = !locked;
        }
        ImGui.PopStyleColor(3);
    }

    void RenderHideRoisButton()
    {
        var color = hideRois ? toggleOnColor : toggleOffColor;
        var hovered = new Vector4(Math.Min(1f, color.X + 0.1f), Math.Min(1f, color.Y + 0.1f), Math.Min(1f, color.Z + 0.1f), 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
        if (ImGui.Button("Hide ROIs##RoiManager", new Vector2(controlButtonWidth, ImGui.GetFrameHeight() * 1.6f)))
        {
            hideRois = !hideRois;
        }
        ImGui.PopStyleColor(3);
    }

    void RenderWarningPopup()
    {
        const string popupId = "Warning##RoiManagerWarning";
        if (warningMessage != null && !ImGui.IsPopupOpen(popupId)) ImGui.OpenPopup(popupId);
        if (ImGui.BeginPopupModal(popupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(warningMessage ?? string.Empty);
            if (ImGui.Button("OK##RoiManagerWarning", new Vector2(120f, 0f)))
            {
                warningMessage = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    static bool RenderCameras(List<ImageSlot> slots, List<RoiMeta> meta, HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled, bool showRois)
    {
        var changed = false;
        if (ImGui.BeginTable("##RoiManagerCameras", slots.Count, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            foreach (var slot in slots)
            {
                ImGui.TableNextColumn();
                changed |= RenderCameraImage(slot, meta, previousActiveKeys, currentActiveKeys, selectedKeys, enabled, showRois);
            }

            ImGui.EndTable();
        }
        return changed;
    }

    static bool RenderCameraImage(ImageSlot slot, List<RoiMeta> meta, HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled, bool showRois)
    {
        if (slot.TextureId == 0 || slot.TextureWidth <= 0 || slot.TextureHeight <= 0)
        {
            ImGui.TextColored(ColorExtensions.CameraColor(slot.Camera), slot.Label + " (no image)");
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
        // Thick border in the camera's color identifies each channel (in place of a title). Drawn
        // inset by half its thickness so it stays inside the image bounds.
        var borderInset = new Vector2(cameraBorderThickness * 0.5f, cameraBorderThickness * 0.5f);
        drawList.AddRect(
            origin + borderInset, origin + displaySize - borderInset,
            ImGui.ColorConvertFloat4ToU32(ColorExtensions.CameraColor(slot.Camera)),
            0f, ImDrawFlags.None, cameraBorderThickness);

        // Channel name in the bottom-left corner, just inside the border, on a dark backing so
        // it stays readable over bright frames. Drawn before the circles so they stay on top.
        var labelPadding = new Vector2(cameraBorderThickness + 4f, cameraBorderThickness + 2f);
        var labelSize = ImGui.CalcTextSize(slot.Label);
        var labelPos = new Vector2(origin.X + labelPadding.X, origin.Y + displaySize.Y - labelPadding.Y - labelSize.Y);
        drawList.AddRectFilled(labelPos - new Vector2(3f, 1f), labelPos + labelSize + new Vector2(3f, 1f), ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.6f)));
        drawList.AddText(labelPos, ImGui.ColorConvertFloat4ToU32(ColorExtensions.CameraColor(slot.Camera)), slot.Label);

        // "Hide ROIs": skip the circles entirely (no drawing, no drag/resize); the image, border
        // and channel name still render.
        if (showRois)
        {
            changed |= RenderCircleOverlay(slot.Circles.Background, "B", null, true, ColorExtensions.BackgroundColor, ColorExtensions.BackgroundColor, "bg", "bg_" + slot.Camera, origin, scale, boundsWidth, boundsHeight, drawList, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
            for (int i = 0; i < slot.Circles.Rois.Count; i++)
            {
                var key = "roi" + i;
                // Outline in the Roi number's color, fill in the fiber number's color.
                changed |= RenderCircleOverlay(slot.Circles.Rois[i], i.ToString(), meta[i].Name, meta[i].Enabled, ColorExtensions.RoiColor(i), ColorExtensions.RoiColor(meta[i].FiberNumber), key, key + "_" + slot.Camera, origin, scale, boundsWidth, boundsHeight, drawList, previousActiveKeys, currentActiveKeys, selectedKeys, enabled);
            }
        }

        // A bare SetCursorScreenPos with no item submitted afterward can't grow the
        // table/window's tracked content bounds; a zero-size Dummy() registers it.
        ImGui.SetCursorScreenPos(afterImage);
        ImGui.Dummy(Vector2.Zero);
        return changed;
    }

    // Circle center/radius are stored in image-pixel space; `scale` converts to/from the
    // on-screen display size. Drag moves the circle, mouse wheel while hovering resizes it.
    // Hover/drag highlighting is tracked per column (`widgetIdSuffix`), so it stays in the camera
    // being interacted with; table row selection does not highlight circles.
    static unsafe bool RenderCircleOverlay(
        PickerCircle circle, string label, string name, bool roiEnabled, Vector4 edgeColor, Vector4 fillColor, string activeKey, string widgetIdSuffix,
        Vector2 origin, float scale, float boundsWidth, float boundsHeight, ImDrawListPtr drawList,
        HashSet<string> previousActiveKeys, HashSet<string> currentActiveKeys, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = ClampToBounds(circle, boundsWidth, boundsHeight);

        var isActive = previousActiveKeys.Contains(widgetIdSuffix);
        // Each Roi has a fixed outline color (its Roi number) and fill color (its fiber number), see
        // ColorExtensions.RoiColor. Hover/drag turns the whole circle green;
        // disabled Rois are drawn grey (still draggable/selectable) so they read as "excluded".
        var color = isActive ? activeCircleColor : roiEnabled ? edgeColor : disabledCircleColor;
        var fill = isActive ? activeCircleColor : roiEnabled ? fillColor : disabledCircleColor;
        var center = origin + new Vector2(circle.X, circle.Y) * scale;
        var radius = circle.Radius * scale;

        drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(new Vector4(fill.X, fill.Y, fill.Z, circleFillAlpha)));
        drawList.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(color), 0, circleStrokeThickness);
        if (!roiEnabled)
        {
            // Cross over the circle, reaching past its edge, so disabled Rois are unmistakable.
            var arm = radius * 1.3f / (float)Math.Sqrt(2.0);
            var crossColor = ImGui.ColorConvertFloat4ToU32(disabledCircleColor);
            drawList.AddLine(center + new Vector2(-arm, -arm), center + new Vector2(arm, arm), crossColor, circleStrokeThickness);
            drawList.AddLine(center + new Vector2(-arm, arm), center + new Vector2(arm, -arm), crossColor, circleStrokeThickness);
        }
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(center - textSize * 0.5f, ImGui.ColorConvertFloat4ToU32(labelColor), label);
        if (!string.IsNullOrEmpty(name))
        {
            var nameSize = ImGui.CalcTextSize(name);
            drawList.AddText(center + new Vector2(-nameSize.X * 0.5f, radius + 2f), ImGui.ColorConvertFloat4ToU32(color), name);
        }

        ImGui.SetCursorScreenPos(center - new Vector2(radius, radius));
        ImGui.InvisibleButton("circle_" + widgetIdSuffix, new Vector2(radius * 2f, radius * 2f));

        // Hover/active tracking (and the resulting green highlight) stays live even
        // when disabled; only the actual drag/resize mutations below are gated on `enabled`.
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active) currentActiveKeys.Add(widgetIdSuffix);

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
    // deleting it applies to all cameras. Row selection does not highlight the circles.
    static bool RenderRoiTable(RoiState state, HashSet<string> selectedKeys, bool enabled)
    {
        var changed = false;

        // Columns mirror AindPhysiologyFip.FipCircle: RoiNumber (the row index), FiberNumber,
        // Name, Enabled - plus an unlabeled swatch, next to the Roi number, showing the Roi's
        // circle color.
        if (ImGui.BeginTable("##RoiTable", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
        {
            var roiWidth = ImGui.CalcTextSize("ROI").X + ImGui.GetFrameHeight() * 0.5f;
            // Room for a 3-digit number plus the two arrow buttons.
            var fiberWidth = ImGui.CalcTextSize("000").X + ImGui.GetFrameHeight() * 2.5f;
            ImGui.TableSetupColumn("ROI", ImGuiTableColumnFlags.WidthFixed, roiWidth);
            ImGui.TableSetupColumn("##Color", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
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

    // Row-wide selectable drawn first in the row, with an empty label so the cell's own widgets
    // render on top of it. AllowOverlap lets those widgets (inputs, checkbox, delete button)
    // reliably take their clicks over the selectable's SpanAllColumns hit region.
    static void RenderRowSelectable(string key, HashSet<string> selectedKeys)
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
        ImGui.SameLine(0f, 0f);
    }

    // Draws a swatch filling the current table cell's width: `fill` inside, `edge` as its border,
    // matching how that Roi's circle is drawn. A non-interactive Dummy reserves the space, so
    // clicks still reach the row's selectable underneath.
    static void RenderColorSwatch(Vector4 fill, Vector4 edge)
    {
        var size = new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), ImGui.GetFrameHeight());
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var inset = new Vector2(swatchBorderThickness * 0.5f, swatchBorderThickness * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(fill));
        drawList.AddRect(min + inset, max - inset, ImGui.ColorConvertFloat4ToU32(edge), 0f, ImDrawFlags.None, swatchBorderThickness);
        ImGui.Dummy(size);
    }

    // Only the Background circles' geometry is used, so their row shows fixed placeholders
    // instead of editable Roi metadata.
    static void RenderBackgroundRow(RoiState state, HashSet<string> selectedKeys)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        RenderRowSelectable("bg", selectedKeys);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("B");

        ImGui.TableNextColumn();
        RenderColorSwatch(ColorExtensions.BackgroundColor, ColorExtensions.BackgroundColor);

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
        RenderRowSelectable(key, selectedKeys);

        // Disabled Rois render their text (and swatch) grey, matching their grey circle in the
        // images.
        // Captured once: a fiber swap below can change meta.Enabled between the push and the pop.
        var rowEnabled = meta.Enabled;
        if (!rowEnabled) ImGui.PushStyleColor(ImGuiCol.Text, disabledCircleColor);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(index.ToString(CultureInfo.InvariantCulture));

        ImGui.TableNextColumn();
        // Fiber color inside, Roi color as the border - same as the circle. Drawn before the fiber
        // input, so a fiber change shows its new color from the next frame.
        if (rowEnabled) RenderColorSwatch(ColorExtensions.RoiColor(meta.FiberNumber), ColorExtensions.RoiColor(index));
        else RenderColorSwatch(disabledCircleColor, disabledCircleColor);

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
        if (!rowEnabled) ImGui.PopStyleColor();

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

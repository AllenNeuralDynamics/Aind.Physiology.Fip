using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Bonsai;
using FipExtensions;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

/// <summary>
/// Represents an operator that renders rolling activity traces for every camera/ROI combination
/// found in a grouped Fip activity stream (e.g. the "ParsedFipStreams" subject, grouped by
/// <see cref="FipCameraSource"/>), across two tabs: "By Channel" (one row per hard-coded camera,
/// overlaying whichever ROI(s) are selected from a shared combo) and "By ROI" (one row per ROI,
/// overlaying all cameras together, scrollable). Source1 = Frame subject; Source2 = the grouped
/// stream.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders rolling activity traces for every camera/ROI combination in a grouped Fip activity stream (e.g. ParsedFipStreams), across a \"By Channel\" tab (one row per hard-coded camera, selected ROI(s) overlaid) and a \"By ROI\" tab (one row per ROI, all cameras overlaid, scrollable). Source1 = Frame subject; Source2 = IObservable<IGroupedObservable<FipCameraSource, Timestamped<CircleActivityCollection>>>.")]
public class FipActivityGridVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private float fontSize = 20f;
    [Description("Font size used to render the control.")]
    public float FontSize { get { return fontSize; } set { fontSize = value; } }

    private double capacity = 10;
    [Description("The rolling time window, in seconds, kept on each plot.")]
    public double Capacity { get { return capacity; } set { capacity = value; } }

    // Hard-coded camera rows for the "By Channel" tab, in display order. Add/remove entries
    // here if the set of cameras changes.
    static readonly FipCameraSource[] cameraOrder = new FipCameraSource[]
    {
        FipCameraSource.Iso,
        FipCameraSource.Green,
        FipCameraSource.Red,
    };

    const float rowHeight = 150f;

    // Region indices in a CircleActivityCollection: the Background first, then ROI 0.
    const int BackgroundIndex = 0;
    const int DefaultRoiIndex = 1;

    // `index` counts the Background as 0 (the CircleActivityCollection order), so ROI n is
    // index n + 1. Labeled "ROI n : F<fiber>" when the Roi has a fiber number.
    static string RoiName(int index, AindPhysiologyFip.FipCircle circle)
    {
        if (index == 0) return "Background";
        var name = "ROI " + (index - 1).ToString();
        if (circle != null && circle.FiberNumber.HasValue) name += " : F" + circle.FiberNumber.Value.ToString();
        return name;
    }

    // Same colors as the circles in RoiManagerVisualizer: the Background is white, and ROI n
    // (region index n + 1) uses the color for Roi number n.
    static Vector4 RoiColor(int index)
    {
        return index == 0 ? ColorExtensions.BackgroundColor : ColorExtensions.RoiColor(index - 1);
    }

    // The Roi definition behind a region index. Fiber numbers are shared by all cameras, so the
    // first camera that has a circle for that index provides it. Call while holding bufferLock.
    static AindPhysiologyFip.FipCircle FindCircle(Dictionary<FipCameraSource, CameraBuffer> buffers, int index)
    {
        foreach (var buffer in buffers.Values)
        {
            if (index < buffer.Circles.Length && buffer.Circles[index] != null) return buffer.Circles[index];
        }
        return null;
    }

    private struct ActivityPoint
    {
        public double Time;
        public double Value;

        public ActivityPoint(double time, double value)
        {
            Time = time;
            Value = value;
        }
    }

    private class CameraBuffer
    {
        public List<ActivityPoint>[] Rois = new List<ActivityPoint>[0];
        // The Roi definition behind each region, from the most recent activity.
        public AindPhysiologyFip.FipCircle[] Circles = new AindPhysiologyFip.FipCircle[0];
    }

    // Keeps exactly one trace per region. When the Roi count changes (Add ROI / delete), the
    // region indices no longer line up with the old traces - deleting ROI 1 makes the old ROI 2
    // the new ROI 1 - so the camera's history is cleared rather than shifted or kept stale.
    static void MatchRoiCount(CameraBuffer buffer, int count)
    {
        if (buffer.Rois.Length == count) return;
        var rois = new List<ActivityPoint>[count];
        for (int i = 0; i < count; i++)
        {
            rois[i] = new List<ActivityPoint>();
        }
        buffer.Rois = rois;
    }

    public IObservable<Unit> Process<TTickSource>(
        IObservable<TTickSource> frames,
        IObservable<System.Reactive.Linq.IGroupedObservable<FipCameraSource, Bonsai.Harp.Timestamped<CircleActivityCollection>>> streams)
    {
        return Observable.Create<Unit>(observer =>
        {
            var buffers = new Dictionary<FipCameraSource, CameraBuffer>();
            var bufferLock = new object();
            // Default to the Background (region index 0) + ROI 0 (region index 1). Safe even if
            // ROI 0 never appears: RenderByChannel skips any selected index >= the known ROI count.
            var selectedRois = new HashSet<int> { BackgroundIndex, DefaultRoiIndex };
            var innerSubs = new CompositeDisposable();

            var streamsSub = streams.SubscribeSafe(Observer.Create<System.Reactive.Linq.IGroupedObservable<FipCameraSource, Bonsai.Harp.Timestamped<CircleActivityCollection>>>(
                group =>
                {
                    var camera = group.Key;
                    CameraBuffer buffer;
                    lock (bufferLock)
                    {
                        if (!buffers.TryGetValue(camera, out buffer))
                        {
                            buffer = new CameraBuffer();
                            buffers[camera] = buffer;
                        }
                    }

                    var innerSub = group.SubscribeSafe(Observer.Create<Bonsai.Harp.Timestamped<CircleActivityCollection>>(
                        activity =>
                        {
                            lock (bufferLock)
                            {
                                var count = activity.Value.Count;
                                MatchRoiCount(buffer, count);
                                buffer.Circles = activity.Value.FipCircles;

                                var time = activity.Seconds;
                                for (int i = 0; i < count; i++)
                                {
                                    var points = buffer.Rois[i];
                                    points.Add(new ActivityPoint(time, activity.Value[i].Activity.Val0));
                                    points.RemoveAll(p => p.Time < time - Capacity);
                                }
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

                    if (!Visible) { observer.OnNext(Unit.Default); return; }

                    int maxRoiCount;
                    string[] roiNames;
                    Vector4[] roiColors;
                    lock (bufferLock)
                    {
                        maxRoiCount = buffers.Values.Select(b => b.Rois.Length).DefaultIfEmpty(0).Max();
                        roiNames = new string[maxRoiCount];
                        roiColors = new Vector4[maxRoiCount];
                        for (int i = 0; i < maxRoiCount; i++)
                        {
                            var circle = FindCircle(buffers, i);
                            roiNames[i] = RoiName(i, circle);
                            roiColors[i] = RoiColor(i);
                        }
                    }

                    // An exception escaping mid-frame would leave ImGui's stacks unbalanced and the
                    // next frame's NewFrame() aborts the whole Bonsai process. Snapshot ImGui's state
                    // so it can be unwound, then surface the exception as a normal workflow error.
                    var recoveryState = new ImGuiErrorRecoveryState();
                    ImGuiP.ErrorRecoveryStoreState(ref recoveryState);
                    try
                    {
                        ImGui.PushFont(ImGui.GetFont(), FontSize);
                        if (ImGui.BeginTabBar("##FipActivityGridTabs"))
                        {
                            if (ImGui.BeginTabItem("By Channel"))
                            {
                                RenderByChannel(buffers, bufferLock, selectedRois, roiNames, roiColors);
                                ImGui.EndTabItem();
                            }
                            if (ImGui.BeginTabItem("By ROI"))
                            {
                                RenderByRoi(buffers, bufferLock, roiNames);
                                ImGui.EndTabItem();
                            }
                            ImGui.EndTabBar();
                        }
                        ImGui.PopFont();
                    }
                    catch (Exception ex)
                    {
                        ImGuiP.ErrorRecoveryTryToRecoverState(ref recoveryState);
                        observer.OnError(ex);
                        return;
                    }

                    observer.OnNext(Unit.Default);
                },
                observer.OnError,
                observer.OnCompleted));

            return new CompositeDisposable(streamsSub, innerSubs, frameSub);
        });
    }

    void RenderByChannel(
        Dictionary<FipCameraSource, CameraBuffer> buffers,
        object bufferLock,
        HashSet<int> selectedRois,
        string[] roiNames,
        Vector4[] roiColors)
    {
        var roiCount = roiNames.Length;
        // Selections are never pruned: the ROI count can dip briefly (e.g. before the ROI settings
        // reach the calculator), and pruning then would silently drop the default selection.
        // Indices past the current ROI count (deleted ROIs) are just not shown or plotted, and come
        // back if the ROI is re-added. Before any data has arrived, label by index alone.
        var visibleSelection = selectedRois.Where(i => roiCount == 0 || i < roiCount).OrderBy(i => i).ToList();
        var comboLabel = visibleSelection.Count == 0 ? "None" : string.Join(", ", visibleSelection.Select(i => i < roiCount ? roiNames[i] : RoiName(i, null)));
        if (ImGui.BeginCombo("ROIs##FipActivityGrid", comboLabel))
        {
            for (int i = 0; i < roiCount; i++)
            {
                var isSelected = selectedRois.Contains(i);
                if (ImGui.Checkbox(roiNames[i] + "##roiSelect" + i.ToString(), ref isSelected))
                {
                    if (isSelected) selectedRois.Add(i);
                    else selectedRois.Remove(i);
                }
            }
            if (roiCount == 0)
            {
                ImGui.TextUnformatted("No ROIs detected yet.");
            }
            ImGui.EndCombo();
        }

        for (int c = 0; c < cameraOrder.Length; c++)
        {
            var camera = cameraOrder[c];
            CameraBuffer buffer;
            var latest = 0.0;
            var snapshots = new List<Tuple<int, ActivityPoint[]>>();

            lock (bufferLock)
            {
                if (buffers.TryGetValue(camera, out buffer))
                {
                    foreach (var roi in selectedRois)
                    {
                        // Also bounded by roiNames: camera threads can grow buffer.Rois after the
                        // names/colors were sized at the start of this frame (e.g. on the first
                        // frames, or right after Add ROI); that ROI is plotted from the next frame.
                        if (roi < buffer.Rois.Length && roi < roiNames.Length)
                        {
                            var points = buffer.Rois[roi].ToArray();
                            snapshots.Add(Tuple.Create(roi, points));
                            if (points.Length > 0) latest = Math.Max(latest, points[points.Length - 1].Time);
                        }
                    }
                }
            }

            if (ImPlot.BeginPlot("##channel" + camera.ToString(), new Vector2(-1, rowHeight), ImPlotFlags.NoTitle))
            {
                SetupRowAxes(ColorExtensions.CameraDisplayName(camera), latest, Capacity, ColorExtensions.CameraColor(camera));
                for (int i = 0; i < snapshots.Count; i++)
                {
                    PlotSeries(roiNames[snapshots[i].Item1], snapshots[i].Item2, roiColors[snapshots[i].Item1]);
                }
                ImPlot.EndPlot();
            }
        }
    }

    void RenderByRoi(Dictionary<FipCameraSource, CameraBuffer> buffers, object bufferLock, string[] roiNames)
    {
        var roiCount = int.MaxValue;
        lock (bufferLock)
        {
            for (int c = 0; c < cameraOrder.Length; c++)
            {
                CameraBuffer buffer;
                var count = buffers.TryGetValue(cameraOrder[c], out buffer) ? buffer.Rois.Length : 0;
                roiCount = Math.Min(roiCount, count);
            }
        }

        ImGui.BeginChild("##FipActivityGridByRoiScroll", new Vector2(-1, -1));
        for (int roi = 0; roi < roiCount; roi++)
        {
            var latest = 0.0;
            var lines = new List<Tuple<FipCameraSource, ActivityPoint[]>>();

            lock (bufferLock)
            {
                for (int c = 0; c < cameraOrder.Length; c++)
                {
                    var camera = cameraOrder[c];
                    CameraBuffer buffer;
                    if (buffers.TryGetValue(camera, out buffer) && roi < buffer.Rois.Length)
                    {
                        var points = buffer.Rois[roi].ToArray();
                        lines.Add(Tuple.Create(camera, points));
                        if (points.Length > 0) latest = Math.Max(latest, points[points.Length - 1].Time);
                    }
                }
            }

            if (ImPlot.BeginPlot("##roi" + roi.ToString(), new Vector2(-1, rowHeight), ImPlotFlags.NoTitle | ImPlotFlags.NoLegend))
            {
                SetupRowAxes(roi < roiNames.Length ? roiNames[roi] : RoiName(roi, null), latest, Capacity, null);
                for (int i = 0; i < lines.Count; i++)
                {
                    PlotSeries(ColorExtensions.CameraDisplayName(lines[i].Item1), lines[i].Item2, ColorExtensions.CameraColor(lines[i].Item1));
                }
                ImPlot.EndPlot();
            }
        }
        ImGui.EndChild();
    }

    static void SetupRowAxes(string yLabel, double latest, double capacity, Vector4? yLabelColor)
    {
        var axesFlags = ImPlotAxisFlags.NoHighlight | ImPlotAxisFlags.NoInitialFit | ImPlotAxisFlags.AutoFit;
        ImPlot.SetupAxis(ImAxis.X1, "Seconds", axesFlags | ImPlotAxisFlags.NoLabel);

        // Colors the Y axis label/ticks so each "By Channel" row is identifiable at a glance;
        // ImPlot bakes the current AxisText style color into the axis at SetupAxis-call time, so
        // push/pop only needs to wrap this one call.
        if (yLabelColor.HasValue) ImPlot.PushStyleColor(ImPlotCol.AxisText, yLabelColor.Value);
        ImPlot.SetupAxis(ImAxis.Y1, yLabel, axesFlags);
        if (yLabelColor.HasValue) ImPlot.PopStyleColor();

        ImPlot.SetupAxisLimits(ImAxis.X1, latest - capacity, latest, ImPlotCond.Always);
        // Label change only: the window always spans [latest - capacity, latest], so show that
        // relative to "now" instead of noisy/imprecise absolute timestamps.
        ImPlot.SetupAxisTicks(ImAxis.X1, latest - capacity, latest, 2, new string[] { "-" + capacity.ToString(), "0" });
    }

    static unsafe void PlotSeries(string label, ActivityPoint[] points, Vector4 color)
    {
        if (points.Length == 0) return;
        ImPlot.SetNextLineStyle(color, 2.0f);
        var xs = new double[points.Length];
        var ys = new double[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            xs[i] = points[i].Time;
            ys[i] = points[i].Value;
        }
        fixed (double* x = xs)
        fixed (double* y = ys)
        {
            ImPlot.PlotLine(label, x, y, points.Length);
        }
    }
}

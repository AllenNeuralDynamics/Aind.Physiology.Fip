using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using FipExtensions;
using Hexa.NET.ImGui;

/// <summary>
/// Represents an operator that renders the light source calibration control for the Fip camera
/// channels found in <see cref="LightSources"/>: a table with a color-filled light source cell, a
/// 0-1 power slider with a real-unit readout, the power currently applied, the experiment power
/// for comparison, a "Set CW" button and an On/Off state driven by <see cref="State"/>; "Off",
/// "Fip Mode" and "Reset" buttons sit to the right of that table. Source1 = Frame subject.
/// </summary>
/// <remarks>
/// "Set CW" emits a <see cref="ContinuousLightSourceTask"/> for that channel only and only affects
/// the live preview slider/readout, not what an experiment will use (see
/// <see cref="FipExperimentPowerVisualizer"/> for that). "Off"/"Fip Mode" emit a single
/// <see cref="OffLightSourceTask"/>/<see cref="FipModeLightSourceTask"/>, since neither carries per-channel
/// data. "Reset" only restores every slider to the rig-config duty cycle and emits nothing.
/// <see cref="State"/> should be fed (via PropertyMapping, from a feedback/readback subject)
/// with whichever task is actually running, since only one <see cref="ITriggerLightSourceTask"/> can
/// be active at a time; buttons/rows highlight themselves based on that, not their own click
/// history. Use <see cref="LightSourceTaskOfType{TResult}"/> downstream to split this operator's mixed
/// output back into a specific concrete type.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the light source calibration control: per light source a color-filled cell, 0-1 power slider, real-unit readout, current (applied) power, experiment power, Set CW button and On/Off state; plus Off / Fip Mode / Reset buttons to the right of the table. Set CW emits a ContinuousLightSourceTask for that channel and only affects the live preview. Off/Fip Mode emit a single OffLightSourceTask/FipModeLightSourceTask. Reset restores all sliders to the rig-config values without emitting anything. State should be fed the currently-running ITriggerLightSourceTask so the active mode/channel can be highlighted. Source1 = Frame subject; LightSources/State are set via PropertyMapping, not wired as Process arguments. Set Enabled to false to stop drawing the control.")]
public class FipCameraPowerControlVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private float fontSize = 26f;
    [Description("Font size used to render the control. Ignored while AutoFontSize is true.")]
    public float FontSize { get { return fontSize; } set { fontSize = value; } }

    private bool autoFontSize = true;
    [Description("When true, the font size is scaled each frame so the control fills the available window space, and FontSize is ignored.")]
    public bool AutoFontSize { get { return autoFontSize; } set { autoFontSize = value; } }

    private bool enabled = true;
    [Description("Specifies whether the control is available. When false, nothing is drawn.")]
    public bool Enabled { get { return enabled; } set { enabled = value; } }

    private static readonly Dictionary<FipCameraSource, CalibratedLightSource> emptyLightSources =
        new Dictionary<FipCameraSource, CalibratedLightSource>();

    /// <summary>
    /// Gets or sets the calibrated light sources to render as rows, keyed by camera channel.
    /// Not serialized: assign via PropertyMapping rather than the property grid.
    /// </summary>
    [XmlIgnore]
    public Dictionary<FipCameraSource, CalibratedLightSource> LightSources { get; set; }

    /// <summary>
    /// Gets or sets the currently-running light source task, used to decide what to highlight as
    /// active. Not serialized: assign via PropertyMapping. Leave null if no feedback is available.
    /// </summary>
    [XmlIgnore]
    public ITriggerLightSourceTask State { get; set; }

    private class RowState
    {
        public float Power;
    }

    public IObservable<ITriggerLightSourceTask> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<ITriggerLightSourceTask>(observer =>
        {
            var rows = new Dictionary<FipCameraSource, RowState>();

            var frameSub = frames.SubscribeSafe(Observer.Create<TTickSource>(
                _ =>
                {
                    // Mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0.
                    unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }
                    FipStyle.Apply();

                    if (!Visible || !Enabled) return;

                    var snapshot = LightSources ?? emptyLightSources;
                    var currentState = State;
                    var activeContinuous = currentState as ContinuousLightSourceTask;

                    // Seed newly-seen rows from the calibrated duty cycle; existing rows are
                    // left alone so we don't fight the user's slider drag.
                    foreach (var pair in snapshot)
                    {
                        if (!rows.ContainsKey(pair.Key))
                        {
                            var row = new RowState();
                            row.Power = pair.Value != null ? (float)pair.Value.CalibratedDutyCycle : 0f;
                            rows[pair.Key] = row;
                        }
                    }

                    // Each row is a single line of widgets; width needs ~40 text lines.
                    var renderFontSize = AutoFontSize ? FipPowerUi.AutoFontSize(snapshot.Count, 1f, 40f) : FipDpi.Px(FontSize);
                    FipPowerUi.PushFont(renderFontSize);
                    Draw(snapshot, rows, currentState, activeContinuous, observer, renderFontSize);
                    ImGui.PopFont();
                },
                observer.OnError,
                observer.OnCompleted));

            return frameSub;
        });
    }

    static readonly Vector4 DarkText = new Vector4(0.08f, 0.08f, 0.08f, 1f);
    static readonly Vector4 LightText = new Vector4(1f, 1f, 1f, 1f);

    // Dark text on light cell colors (Lime), white on dark ones (UV, Blue).
    static Vector4 ContrastText(Vector4 background)
    {
        var luminance = 0.299f * background.X + 0.587f * background.Y + 0.114f * background.Z;
        return luminance > 0.55f ? DarkText : LightText;
    }

    static Vector4 WithAlpha(Vector4 color, float alpha)
    {
        return new Vector4(color.X, color.Y, color.Z, alpha);
    }

    // One row per light source, every widget on a single line:
    //   Light | CW Power slider (duty cycle + real-unit readout) | Applied | Experiment | Set CW | State
    // with the Off / Fip Mode / Reset buttons stacked to the right of the table.
    static void Draw(
        Dictionary<FipCameraSource, CalibratedLightSource> snapshot,
        Dictionary<FipCameraSource, RowState> rows,
        ITriggerLightSourceTask currentState,
        ContinuousLightSourceTask activeContinuous,
        IObserver<ITriggerLightSourceTask> observer,
        float renderFontSize)
    {
        var itemSpacing = FipDpi.ItemSpacing;
        var buttonColumnWidth = renderFontSize * 6f;
        var tableWidth = ImGui.GetContentRegionAvail().X - buttonColumnWidth - itemSpacing.X;
        FipPowerUi.ComputeRowHeight(snapshot.Count);

        ImGui.BeginGroup();
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##FipCameraPowerControl", 6, tableFlags, new Vector2(tableWidth, 0)))
        {
            ImGui.TableSetupColumn("Light", ImGuiTableColumnFlags.WidthFixed, FipDpi.FitWidth("Light", 0f));
            ImGui.TableSetupColumn("CW Power", ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn("Applied", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Experiment", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("##SetCW", ImGuiTableColumnFlags.WidthFixed, FipDpi.FitWidth("Set CW", 0f));
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, FipDpi.FitWidth("State", 0f));
            ImGui.TableHeadersRow();

            foreach (var pair in snapshot)
            {
                var channel = pair.Key;
                var lightSource = pair.Value;
                var row = rows[channel];
                var idSuffix = "##" + channel.ToString();
                var isActiveChannel = activeContinuous != null && activeContinuous.Channel == channel;

                ImGui.TableNextRow(ImGuiTableRowFlags.None, FipPowerUi.RowHeight);

                // Light source name on its own color.
                ImGui.TableNextColumn();
                var lightColor = ColorExtensions.LightSourceColor(channel);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(lightColor));
                FipPowerUi.CenteredText(ColorExtensions.LightSourceName(channel), ContrastText(lightColor));

                // Unsent: an active channel whose slider differs from what is applied, or an off
                // channel whose slider was moved away from the rig-config duty cycle.
                var reference = isActiveChannel
                    ? activeContinuous.Power
                    : (lightSource != null ? (float)lightSource.CalibratedDutyCycle : 0f);
                var pending = Math.Abs(row.Power - reference) > 1e-4f;

                // Duty cycle slider; the real-unit power is shown inside it (ImGui format string,
                // so a literal % is escaped). Ctrl+click types an exact value. Orange while unsent.
                ImGui.TableNextColumn();
                FipPowerUi.VerticalCenter(ImGui.GetFrameHeight());
                var readout = FipPowerUi.FormatPower(lightSource, row.Power).Replace("%", "%%");
                var format = "%.3f   " + readout + (pending ? "   (not set)" : string.Empty);
                if (pending)
                {
                    ImGui.PushStyleColor(ImGuiCol.FrameBg, WithAlpha(FipPowerUi.PendingColor, 0.25f));
                    ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, WithAlpha(FipPowerUi.PendingColor, 0.35f));
                    ImGui.PushStyleColor(ImGuiCol.FrameBgActive, WithAlpha(FipPowerUi.PendingColor, 0.45f));
                    ImGui.PushStyleColor(ImGuiCol.SliderGrab, FipPowerUi.PendingColor);
                }
                ImGui.SetNextItemWidth(-1f);
                var power = row.Power;
                if (ImGui.SliderFloat("##Power" + idSuffix, ref power, 0f, 1f, format, ImGuiSliderFlags.AlwaysClamp))
                {
                    row.Power = power;
                }
                if (pending) ImGui.PopStyleColor(4);
                // On release (or Enter after Ctrl+click), push the new power if this channel is already on.
                if (ImGui.IsItemDeactivatedAfterEdit() && isActiveChannel)
                {
                    observer.OnNext(new ContinuousLightSourceTask(channel, row.Power));
                }
                ImGui.SetItemTooltip("Duty cycle (0-1). Drag, or Ctrl+click to type a value.\nApplied with Set CW, or on release while this light is on.");

                // What is actually applied right now (the slider only takes effect on Set CW).
                ImGui.TableNextColumn();
                if (isActiveChannel)
                {
                    FipPowerUi.CenteredText(FipPowerUi.FormatPower(lightSource, activeContinuous.Power), FipPowerUi.ActiveColor);
                }
                else if (currentState is FipModeLightSourceTask && lightSource != null)
                {
                    FipPowerUi.CenteredText(FipPowerUi.FormatPower(lightSource, (float)lightSource.CalibratedDutyCycle), FipPowerUi.ActiveColor);
                }
                else
                {
                    FipPowerUi.CenteredText("Off", FipPowerUi.InactiveColor);
                }

                // What Start Experiment will use, independent of the slider.
                ImGui.TableNextColumn();
                FipPowerUi.CenteredText(FipPowerUi.FormatPower(lightSource, lightSource != null ? (float)lightSource.CalibratedDutyCycle : 0f), null);

                ImGui.TableNextColumn();
                FipPowerUi.VerticalCenter(ImGui.GetFrameHeight());
                var highlight = pending || isActiveChannel;
                if (highlight) ImGui.PushStyleColor(ImGuiCol.Button, pending ? FipPowerUi.PendingColor : FipPowerUi.ActiveColor);
                if (ImGui.Button("Set CW" + idSuffix, new Vector2(-1f, 0f)))
                {
                    observer.OnNext(new ContinuousLightSourceTask(channel, row.Power));
                }
                if (highlight) ImGui.PopStyleColor();

                ImGui.TableNextColumn();
                // Cell painted blue when on, grey when off, with centered text.
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(isActiveChannel ? FipPowerUi.StateOnColor : FipPowerUi.StateOffColor));
                FipPowerUi.CenteredText(isActiveChannel ? "On" : "Off", LightText);
            }

            ImGui.EndTable();
        }
        ImGui.EndGroup();

        var tableHeight = ImGui.GetItemRectSize().Y;
        ImGui.SameLine();

        // Buttons stacked to the right, spanning the table's height.
        ImGui.BeginGroup();
        var buttonHeight = (tableHeight - 2f * itemSpacing.Y) / 3f;
        var buttonSize = new Vector2(buttonColumnWidth, buttonHeight);

        // Off always stays plain/default colored; its role is to grey out the other
        // buttons via their own State checks, not to show a state of its own.
        // Stops the device altogether (no light, no camera triggers), unlike OffLightSourceTask,
        // which keeps the Fip cycle running in the dark for background frames.
        if (ImGui.Button("Off##FipCameraPowerControl", buttonSize))
        {
            observer.OnNext(new StopLightSourceTask());
        }

        var isFipMode = currentState is FipModeLightSourceTask;
        var fipModeLabel = isFipMode ? "Fip Mode (Active)##FipCameraPowerControl" : "Fip Mode##FipCameraPowerControl";
        if (isFipMode) ImGui.PushStyleColor(ImGuiCol.Button, FipPowerUi.ActiveColor);
        if (ImGui.Button(fipModeLabel, buttonSize))
        {
            observer.OnNext(new FipModeLightSourceTask());
        }
        if (isFipMode) ImGui.PopStyleColor();

        // Local only: restores sliders to the rig config, does not touch the hardware.
        if (ImGui.Button("Reset##FipCameraPowerControl", buttonSize))
        {
            foreach (var pair in snapshot)
            {
                rows[pair.Key].Power = pair.Value != null ? (float)pair.Value.CalibratedDutyCycle : 0f;
            }
        }
        ImGui.EndGroup();
    }
}

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
/// Represents an operator that renders the laser calibration control for the Fip camera
/// channels found in <see cref="LightSources"/>: a table with a color-filled light source cell, a
/// 0-1 power slider with a real-unit readout, the power currently applied, the experiment power
/// for comparison, a "Set CW" button and an On/Off state driven by <see cref="State"/>; "Off",
/// "Fip Mode" and "Reset" buttons sit to the right of that table. Source1 = Frame subject.
/// </summary>
/// <remarks>
/// "Set CW" emits a <see cref="ContinuousLaserTask"/> for that channel only and only affects
/// the live preview slider/readout, not what an experiment will use (see
/// <see cref="FipExperimentPowerVisualizer"/> for that). "Off"/"Fip Mode" emit a single
/// <see cref="OffLaserTask"/>/<see cref="FipModeLaserTask"/>, since neither carries per-channel
/// data. "Reset" only restores every slider to the rig-config duty cycle and emits nothing.
/// <see cref="State"/> should be fed (via PropertyMapping, from a feedback/readback subject)
/// with whichever task is actually running, since only one <see cref="ITriggerLaserTask"/> can
/// be active at a time; buttons/rows highlight themselves based on that, not their own click
/// history. Use <see cref="LaserTaskOfType{TResult}"/> downstream to split this operator's mixed
/// output back into a specific concrete type.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the laser calibration control: per light source a color-filled cell, 0-1 power slider, real-unit readout, current (applied) power, experiment power, Set CW button and On/Off state; plus Off / Fip Mode / Reset buttons to the right of the table. Set CW emits a ContinuousLaserTask for that channel and only affects the live preview. Off/Fip Mode emit a single OffLaserTask/FipModeLaserTask. Reset restores all sliders to the rig-config values without emitting anything. State should be fed the currently-running ITriggerLaserTask so the active mode/channel can be highlighted. Source1 = Frame subject; LightSources/State are set via PropertyMapping, not wired as Process arguments. Set Enabled to false to stop drawing the control.")]
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
    /// Gets or sets the currently-running laser task, used to decide what to highlight as
    /// active. Not serialized: assign via PropertyMapping. Leave null if no feedback is available.
    /// </summary>
    [XmlIgnore]
    public ITriggerLaserTask State { get; set; }

    private class RowState
    {
        public float Power;
    }

    public IObservable<ITriggerLaserTask> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<ITriggerLaserTask>(observer =>
        {
            var rows = new Dictionary<FipCameraSource, RowState>();

            var frameSub = frames.SubscribeSafe(Observer.Create<TTickSource>(
                _ =>
                {
                    // Mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0.
                    unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }

                    if (!Visible || !Enabled) return;

                    var snapshot = LightSources ?? emptyLightSources;
                    var currentState = State;
                    var activeContinuous = currentState as ContinuousLaserTask;

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

                    // Rough layout model: a row is ~2.7 lines tall (slider + readout), the header
                    // adds about one more row; width needs ~38 lines.
                    var renderFontSize = AutoFontSize ? FipPowerUi.AutoFontSize(snapshot.Count, 2.7f, 38f) : FontSize;
                    FipPowerUi.PushFont(renderFontSize);
                    Draw(snapshot, rows, currentState, activeContinuous, observer, renderFontSize);
                    ImGui.PopFont();
                },
                observer.OnError,
                observer.OnCompleted));

            return frameSub;
        });
    }

    static void Draw(
        Dictionary<FipCameraSource, CalibratedLightSource> snapshot,
        Dictionary<FipCameraSource, RowState> rows,
        ITriggerLaserTask currentState,
        ContinuousLaserTask activeContinuous,
        IObserver<ITriggerLaserTask> observer,
        float renderFontSize)
    {
        // ImGuiStylePtr.ItemSpacing isn't supported by Bonsai's script-extensions
        // compiler (CS0570); use a fixed estimate instead.
        const float itemSpacing = 8f;
        var buttonColumnWidth = renderFontSize * 7f;
        var tableWidth = ImGui.GetContentRegionAvail().X - buttonColumnWidth - itemSpacing;
        FipPowerUi.ComputeRowHeight(snapshot.Count);

        ImGui.BeginGroup();
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##FipCameraPowerControl", 6, tableFlags, new Vector2(tableWidth, 0)))
        {
            ImGui.TableSetupColumn("Light", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Power", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Current Power", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Experiment", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Set CW", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableHeadersRow();

            foreach (var pair in snapshot)
            {
                var channel = pair.Key;
                var lightSource = pair.Value;
                var row = rows[channel];
                var idSuffix = "##" + channel.ToString();
                var isActiveChannel = activeContinuous != null && activeContinuous.Channel == channel;

                ImGui.TableNextRow(ImGuiTableRowFlags.None, FipPowerUi.RowHeight);

                // Color the cell instead of printing the light source name.
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(ColorExtensions.LightSourceColor(channel)));

                ImGui.TableNextColumn();
                FipPowerUi.VerticalCenter(ImGui.GetFrameHeight() + ImGui.GetTextLineHeight() + 4f);
                ImGui.SetNextItemWidth(-1);
                var power = row.Power;
                if (ImGui.SliderFloat("Power" + idSuffix, ref power, 0f, 1f))
                {
                    row.Power = power;
                }
                ImGui.TextUnformatted(FipPowerUi.FormatPower(lightSource, row.Power));

                // What is actually applied right now (the slider only takes effect on Set CW).
                ImGui.TableNextColumn();
                if (isActiveChannel)
                {
                    FipPowerUi.CenteredText(FipPowerUi.FormatPower(lightSource, activeContinuous.Power), FipPowerUi.ActiveColor);
                }
                else if (currentState is FipModeLaserTask && lightSource != null)
                {
                    FipPowerUi.CenteredText(lightSource.CalibratedPower.ToString("F1") + " µW", FipPowerUi.ActiveColor);
                }
                else
                {
                    FipPowerUi.CenteredText("Off", FipPowerUi.InactiveColor);
                }

                // What Start Experiment will use, independent of the slider.
                ImGui.TableNextColumn();
                FipPowerUi.CenteredText(lightSource != null ? lightSource.CalibratedPower.ToString("F1") + " µW" : "-", null);

                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Button, isActiveChannel ? FipPowerUi.ActiveColor : FipPowerUi.InactiveColor);
                if (ImGui.Button("Set CW" + idSuffix, new Vector2(ImGui.GetContentRegionAvail().X, Math.Max(0f, FipPowerUi.RowHeight - 8f))))
                {
                    observer.OnNext(new ContinuousLaserTask(channel, row.Power));
                }
                ImGui.PopStyleColor();

                ImGui.TableNextColumn();
                // Cell painted blue when on, grey when off, with centered text.
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(isActiveChannel ? FipPowerUi.StateOnColor : FipPowerUi.StateOffColor));
                FipPowerUi.CenteredText(isActiveChannel ? "On" : "Off", new Vector4(1f, 1f, 1f, 1f));
            }

            ImGui.EndTable();
        }
        ImGui.EndGroup();

        var tableHeight = ImGui.GetItemRectSize().Y;
        ImGui.SameLine();

        // Buttons stacked to the right, spanning the table's height.
        ImGui.BeginGroup();
        var buttonHeight = (tableHeight - 2f * itemSpacing) / 3f;
        var buttonSize = new Vector2(buttonColumnWidth, buttonHeight);

        // Off always stays plain/default colored; its role is to grey out the other
        // buttons via their own State checks, not to show a state of its own.
        if (ImGui.Button("Off##FipCameraPowerControl", buttonSize))
        {
            observer.OnNext(new OffLaserTask());
        }

        var isFipMode = currentState is FipModeLaserTask;
        var fipModeLabel = isFipMode ? "Fip Mode (Active)##FipCameraPowerControl" : "Fip Mode##FipCameraPowerControl";
        ImGui.PushStyleColor(ImGuiCol.Button, isFipMode ? FipPowerUi.ActiveColor : FipPowerUi.InactiveColor);
        if (ImGui.Button(fipModeLabel, buttonSize))
        {
            observer.OnNext(new FipModeLaserTask());
        }
        ImGui.PopStyleColor();

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

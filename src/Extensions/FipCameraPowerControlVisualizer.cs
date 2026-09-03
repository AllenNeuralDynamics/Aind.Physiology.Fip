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
/// Represents an operator that renders one row per Fip camera channel found in
/// <see cref="LightSources"/>: name, light source, a 0-1 power slider with a real-unit readout,
/// a "Set Power" button, and an On/Off state driven by <see cref="State"/>. A global "Off"/"Fip
/// Mode" button row below the table acts on every channel at once. Source1 = Frame subject.
/// </summary>
/// <remarks>
/// "Set Power" emits a <see cref="ContinuousLaserTask"/> for that channel only. "Off"/"Fip Mode"
/// emit a single <see cref="OffLaserTask"/>/<see cref="FipModeLaserTask"/>, since neither carries
/// per-channel data. <see cref="State"/> should be fed (via PropertyMapping, from a
/// feedback/readback subject) with whichever task is actually running, since only one
/// <see cref="ITriggerLaserTask"/> can be active at a time; buttons/rows highlight themselves
/// based on that, not their own click history. Use <see cref="LaserTaskOfType{TResult}"/>
/// downstream to split this operator's mixed output back into a specific concrete type.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders a power-control row (name, light source, 0-1 power slider, real-unit readout, Set Power button, state) per Fip camera channel found in LightSources, plus global Off/Fip Mode buttons. Set Power emits a ContinuousLaserTask for that channel; Off/Fip Mode emit a single OffLaserTask/FipModeLaserTask. State should be fed the currently-running ITriggerLaserTask so the active mode/channel can be highlighted. Source1 = Frame subject; LightSources/State are set via PropertyMapping, not wired as Process arguments. Set Enabled to false to disable the slider and buttons.")]
public class FipCameraPowerControlVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private float fontSize = 20f;
    [Description("Font size used to render the control.")]
    public float FontSize { get { return fontSize; } set { fontSize = value; } }

    private bool enabled = true;
    [Description("Specifies whether the control accepts user input. When false, the power slider and all buttons are disabled and ignore input.")]
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

    static readonly Vector4 activeButtonColor = new Vector4(0.20f, 0.80f, 0.20f, 1f);
    static readonly Vector4 inactiveButtonColor = new Vector4(0.35f, 0.35f, 0.35f, 0.6f);

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

                    if (!Visible) return;

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

                    ImGui.PushFont(ImGui.GetFont(), FontSize);
                    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
                    if (ImGui.BeginTable("##FipCameraPowerControl", 5, tableFlags))
                    {
                        ImGui.TableSetupColumn("Name");
                        ImGui.TableSetupColumn("Light Source");
                        ImGui.TableSetupColumn("Power");
                        ImGui.TableSetupColumn("Set Power");
                        ImGui.TableSetupColumn("State");
                        ImGui.TableHeadersRow();

                        foreach (var pair in snapshot)
                        {
                            var channel = pair.Key;
                            var lightSource = pair.Value;
                            var row = rows[channel];
                            var idSuffix = "##" + channel.ToString();
                            var isActiveChannel = activeContinuous != null && activeContinuous.Channel == channel;

                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            ImGui.TextColored(ColorExtensions.CameraColor(channel), channel.ToString());

                            ImGui.TableNextColumn();
                            ImGui.TextColored(ColorExtensions.LightSourceColor(channel), ColorExtensions.LightSourceName(channel));

                            ImGui.BeginDisabled(!Enabled);

                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(-1);
                            var power = row.Power;
                            if (ImGui.SliderFloat("Power" + idSuffix, ref power, 0f, 1f))
                            {
                                row.Power = power;
                            }

                            // Uncalibrated DutyCycleToPower is just the unity LUT, so show that
                            // case as a percentage rather than fake mW.
                            if (lightSource != null)
                            {
                                var realPower = lightSource.DutyCycleToPower.Interpolate(row.Power);
                                var isCalibrated = lightSource.LightSource != null && lightSource.LightSource.Calibration != null;
                                var readout = isCalibrated
                                    ? realPower.ToString("F3") + " mW"
                                    : (realPower * 100).ToString("F1") + "%";
                                ImGui.TextUnformatted(readout);
                            }
                            else
                            {
                                ImGui.TextUnformatted("-");
                            }

                            ImGui.TableNextColumn();
                            var setPowerLabel = isActiveChannel ? "Set Power (Active)" + idSuffix : "Set Power" + idSuffix;
                            ImGui.PushStyleColor(ImGuiCol.Button, isActiveChannel ? activeButtonColor : inactiveButtonColor);
                            if (ImGui.Button(setPowerLabel, new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                            {
                                observer.OnNext(new ContinuousLaserTask(channel, row.Power));
                            }
                            ImGui.PopStyleColor();

                            ImGui.EndDisabled();

                            ImGui.TableNextColumn();
                            ImGui.TextColored(isActiveChannel ? activeButtonColor : inactiveButtonColor, isActiveChannel ? "On" : "Off");
                        }

                        ImGui.EndTable();
                    }

                    ImGui.BeginDisabled(!Enabled);
                    // ImGuiStylePtr.ItemSpacing isn't supported by Bonsai's script-extensions
                    // compiler (CS0570); use a fixed estimate instead.
                    const float itemSpacing = 8f;
                    var buttonWidth = (ImGui.GetContentRegionAvail().X - itemSpacing) / 2f;
                    var buttonHeight = ImGui.GetFrameHeight() * 2f;
                    var buttonSize = new Vector2(buttonWidth, buttonHeight);

                    // Off always stays plain/default colored; its role is to grey out the other
                    // buttons via their own State checks, not to show a state of its own.
                    if (ImGui.Button("Off##FipCameraPowerControl", buttonSize))
                    {
                        observer.OnNext(new OffLaserTask());
                    }

                    ImGui.SameLine();

                    var isFipMode = currentState is FipModeLaserTask;
                    var fipModeLabel = isFipMode ? "Fip Mode (Active)##FipCameraPowerControl" : "Fip Mode##FipCameraPowerControl";
                    ImGui.PushStyleColor(ImGuiCol.Button, isFipMode ? activeButtonColor : inactiveButtonColor);
                    if (ImGui.Button(fipModeLabel, buttonSize))
                    {
                        observer.OnNext(new FipModeLaserTask());
                    }
                    ImGui.PopStyleColor();

                    ImGui.EndDisabled();

                    ImGui.PopFont();
                },
                observer.OnError,
                observer.OnCompleted));

            return frameSub;
        });
    }
}

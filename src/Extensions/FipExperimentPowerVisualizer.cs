using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using FipExtensions;
using Hexa.NET.ImGui;

/// <summary>
/// Represents an operator that renders one row per Fip camera channel found in
/// <see cref="LightSources"/> with its channel and light names in their colors and two editable,
/// mutually-locked values: the power an experiment will use and the duty cycle that produces it.
/// Source1 = Frame subject.
/// </summary>
/// <remarks>
/// The rows always show <see cref="LightSources"/> (the CalibratedLightSources subject, which already
/// carries the task's default powers); the only local state is the value being typed or dragged.
/// Power is in microwatts for calibrated light sources and in percent otherwise. Editing either
/// field updates the other through the light source's calibration, and both are clamped to the
/// range the light source can reach. Each committed edit (Enter, focus loss or slider release) emits
/// a copy of <see cref="LightSources"/> with the edited channel rebuilt at the new duty cycle; feed it
/// back into the same subject. Power only applies to Fip mode, so values can only be changed while
/// <see cref="State"/> is Fip mode and <see cref="Enabled"/> is true.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders one row per Fip camera channel found in LightSources (channel and light names in their colors, editable power and duty cycle locked to each other through the calibration). Each committed edit emits LightSources with the edit applied. Source1 = Frame subject; LightSources and State are set via PropertyMapping. Values can only be changed in Fip mode while Enabled is true.")]
public class FipExperimentPowerVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private bool enabled = true;
    [Description("Specifies whether the values can be changed. When false, the control is still displayed but all inputs are disabled.")]
    public bool Enabled { get { return enabled; } set { enabled = value; } }

    private float fontSize = 26f;
    [Description("Font size used to render the control. Ignored while AutoFontSize is true.")]
    public float FontSize { get { return fontSize; } set { fontSize = value; } }

    private bool autoFontSize = true;
    [Description("When true, the font size is scaled each frame so the control fills the available window space, and FontSize is ignored.")]
    public bool AutoFontSize { get { return autoFontSize; } set { autoFontSize = value; } }

    private static readonly IDictionary<FipCameraSource, CalibratedLightSource> emptyLightSources =
        new Dictionary<FipCameraSource, CalibratedLightSource>();

    /// <summary>
    /// Gets or sets the calibrated light sources to render as rows, keyed by camera channel.
    /// Not serialized: assign via PropertyMapping rather than the property grid.
    /// </summary>
    [XmlIgnore]
    public IDictionary<FipCameraSource, CalibratedLightSource> LightSources { get; set; }

    /// <summary>
    /// Gets or sets the light source task currently driving the device. Power is only meaningful in
    /// Fip mode, so the values can only be changed while this is a <see cref="FipModeLightSourceTask"/>.
    /// Not serialized: assign via PropertyMapping rather than the property grid.
    /// </summary>
    [XmlIgnore]
    [Description("The light source task currently driving the device. Values can only be changed in Fip mode. Set via PropertyMapping, not wired as a Process argument.")]
    public ITriggerLightSourceTask State { get; set; }

    // Value of a row while it is being typed or dragged. Power is in display units
    // (microwatts, or percent when uncalibrated).
    private class EditState
    {
        public float Power;
        public float Duty;
    }

    public IObservable<IDictionary<FipCameraSource, CalibratedLightSource>> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<IDictionary<FipCameraSource, CalibratedLightSource>>(observer =>
        {
            var edits = new Dictionary<FipCameraSource, EditState>();

            return frames.SubscribeSafe(Observer.Create<TTickSource>(
                _ =>
                {
                    // Mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0.
                    unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }

                    if (!Visible) return;

                    var lightSources = LightSources ?? emptyLightSources;

                    // Power only applies to Fip mode: edits are allowed only while Fip mode is running
                    // (so they are applied live) and no experiment is running.
                    var isFipMode = State is FipModeLightSourceTask;
                    var editable = Enabled && isFipMode;
                    if (!editable) edits.Clear();

                    var renderFontSize = AutoFontSize ? FipPowerUi.AutoFontSize(lightSources.Count, 1.6f, 30f) : FontSize;
                    FipPowerUi.PushFont(renderFontSize);

                    if (Enabled && !isFipMode)
                    {
                        ImGui.TextColored(FipPowerUi.InactiveColor, "Select Fip Mode in the Calibration tab to change the power.");
                    }

                    FipPowerUi.ComputeRowHeight(lightSources.Count);
                    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
                    if (ImGui.BeginTable("##FipExperimentPower", 4, tableFlags))
                    {
                        ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                        ImGui.TableSetupColumn("Light", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                        ImGui.TableSetupColumn("Power", ImGuiTableColumnFlags.WidthStretch, 1.5f);
                        ImGui.TableSetupColumn("Duty Cycle", ImGuiTableColumnFlags.WidthStretch, 2f);
                        ImGui.TableHeadersRow();

                        IDictionary<FipCameraSource, CalibratedLightSource> committedUpdate = null;
                        foreach (var pair in lightSources)
                        {
                            var channel = pair.Key;
                            var lightSource = pair.Value;

                            ImGui.TableNextRow(ImGuiTableRowFlags.None, FipPowerUi.RowHeight);

                            ImGui.TableNextColumn();
                            FipPowerUi.CenteredText(channel.ToString(), ColorExtensions.CameraColor(channel));

                            ImGui.TableNextColumn();
                            FipPowerUi.CenteredText(ColorExtensions.LightSourceName(channel), ColorExtensions.LightSourceColor(channel));

                            if (lightSource == null)
                            {
                                ImGui.TableNextColumn();
                                FipPowerUi.CenteredText("-", null);
                                ImGui.TableNextColumn();
                                FipPowerUi.CenteredText("-", null);
                                continue;
                            }

                            // Power is edited in microwatts when calibrated, percent otherwise.
                            var calibrated = FipPowerUi.IsCalibrated(lightSource);
                            var scale = calibrated ? 1.0 : 100.0;
                            var minPower = (float)(Math.Min(lightSource.DutyCycleToPower.Interpolate(0), lightSource.DutyCycleToPower.Interpolate(1)) * scale);
                            var maxPower = (float)(Math.Max(lightSource.DutyCycleToPower.Interpolate(0), lightSource.DutyCycleToPower.Interpolate(1)) * scale);

                            EditState edit;
                            var isEditing = edits.TryGetValue(channel, out edit);
                            var power = isEditing ? edit.Power : (float)(lightSource.CalibratedPower * scale);
                            var duty = isEditing ? edit.Duty : (float)lightSource.CalibratedDutyCycle;
                            var committed = false;

                            ImGui.BeginDisabled(!editable);

                            ImGui.TableNextColumn();
                            FipPowerUi.VerticalCenter(ImGui.GetFrameHeight());
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.InputFloat("Power##" + channel.ToString(), ref power, 0f, 0f, calibrated ? "%.1f uW" : "%.1f%%"))
                            {
                                power = Math.Max(minPower, Math.Min(maxPower, power));
                                duty = (float)Math.Max(0, Math.Min(1, lightSource.PowerToDutyCycle.Interpolate(power / scale)));
                                edits[channel] = new EditState { Power = power, Duty = duty };
                            }
                            committed |= ImGui.IsItemDeactivatedAfterEdit();

                            ImGui.TableNextColumn();
                            FipPowerUi.VerticalCenter(ImGui.GetFrameHeight());
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.SliderFloat("Duty##" + channel.ToString(), ref duty, 0f, 1f, "%.3f"))
                            {
                                duty = Math.Max(0f, Math.Min(1f, duty));
                                power = (float)(lightSource.DutyCycleToPower.Interpolate(duty) * scale);
                                edits[channel] = new EditState { Power = power, Duty = duty };
                            }
                            committed |= ImGui.IsItemDeactivatedAfterEdit();

                            ImGui.EndDisabled();

                            if (committed && edits.TryGetValue(channel, out edit))
                            {
                                edits.Remove(channel);
                                if (committedUpdate == null)
                                {
                                    committedUpdate = new Dictionary<FipCameraSource, CalibratedLightSource>(lightSources);
                                }
                                committedUpdate[channel] = new CalibratedLightSource(
                                    lightSource.LightSource, lightSource.DutyCycleToPower, lightSource.PowerToDutyCycle, edit.Duty);
                            }
                        }

                        ImGui.EndTable();

                        // Emit after drawing; the subject feeds the new values back into LightSources.
                        if (committedUpdate != null)
                        {
                            LightSources = committedUpdate;
                            observer.OnNext(committedUpdate);
                        }
                    }

                    ImGui.PopFont();
                },
                observer.OnError,
                observer.OnCompleted));
        });
    }
}

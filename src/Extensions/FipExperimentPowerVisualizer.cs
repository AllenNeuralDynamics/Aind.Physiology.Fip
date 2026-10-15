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
/// <see cref="LightSources"/> with its color-filled channel and light cells and two editable,
/// mutually-locked values: the power an experiment will use and the duty cycle that produces it.
/// Source1 = Frame subject.
/// </summary>
/// <remarks>
/// Power is in microwatts for calibrated light sources and in percent otherwise. Editing either
/// field updates the other through the light source's calibration, and both are clamped to the
/// range the light source can reach. Each committed edit (Enter, focus loss or slider release) emits a new
/// dictionary with every channel's <see cref="CalibratedLightSource"/>, the edited one rebuilt
/// with the resulting duty cycle. Nothing is emitted until the user edits something. Assigning a
/// new <see cref="LightSources"/> instance discards pending edits and reloads the rows from it.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders one row per Fip camera channel found in LightSources (color-filled channel and light cells, editable experiment power in microwatts, and editable duty cycle; the two are locked to each other through the calibration). Each committed edit emits a Dictionary<FipCameraSource, CalibratedLightSource> with the edit applied. Source1 = Frame subject; LightSources is set via PropertyMapping, not wired as a Process argument. Set Enabled to false to display the values without allowing changes.")]
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

    private static readonly Dictionary<FipCameraSource, CalibratedLightSource> emptyLightSources =
        new Dictionary<FipCameraSource, CalibratedLightSource>();

    /// <summary>
    /// Gets or sets the calibrated light sources to render as rows, keyed by camera channel.
    /// Not serialized: assign via PropertyMapping rather than the property grid.
    /// </summary>
    [XmlIgnore]
    public Dictionary<FipCameraSource, CalibratedLightSource> LightSources { get; set; }

    // Pending (uncommitted) values of a row being edited. Power is in display units
    // (microwatts, or percent when uncalibrated).
    private class EditState
    {
        public float Power;
        public float Duty;
    }

    public IObservable<Dictionary<FipCameraSource, CalibratedLightSource>> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<Dictionary<FipCameraSource, CalibratedLightSource>>(observer =>
        {
            Dictionary<FipCameraSource, CalibratedLightSource> source = null;
            Dictionary<FipCameraSource, CalibratedLightSource> current = emptyLightSources;
            var edits = new Dictionary<FipCameraSource, EditState>();

            var frameSub = frames.SubscribeSafe(Observer.Create<TTickSource>(
                _ =>
                {
                    // Mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0.
                    unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }

                    if (!Visible) return;

                    // A new input instance resets the edit state to what it contains.
                    var input = LightSources ?? emptyLightSources;
                    if (!ReferenceEquals(input, source))
                    {
                        source = input;
                        current = input;
                        edits.Clear();
                    }

                    var renderFontSize = AutoFontSize ? FipPowerUi.AutoFontSize(current.Count, 1.6f, 30f) : FontSize;
                    FipPowerUi.PushFont(renderFontSize);

                    FipPowerUi.ComputeRowHeight(current.Count);
                    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
                    if (ImGui.BeginTable("##FipExperimentPower", 4, tableFlags))
                    {
                        ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                        ImGui.TableSetupColumn("Light", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                        ImGui.TableSetupColumn("Power (µW)", ImGuiTableColumnFlags.WidthStretch, 1.5f);
                        ImGui.TableSetupColumn("Duty Cycle", ImGuiTableColumnFlags.WidthStretch, 2f);
                        ImGui.TableHeadersRow();

                        foreach (var pair in current)
                        {
                            var channel = pair.Key;
                            var lightSource = pair.Value;

                            ImGui.TableNextRow(ImGuiTableRowFlags.None, FipPowerUi.RowHeight);

                            ImGui.TableNextColumn();
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(ColorExtensions.CameraColor(channel)));

                            ImGui.TableNextColumn();
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ImGui.ColorConvertFloat4ToU32(ColorExtensions.LightSourceColor(channel)));

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

                            ImGui.BeginDisabled(!Enabled);

                            ImGui.TableNextColumn();
                            FipPowerUi.VerticalCenter(ImGui.GetFrameHeight());
                            ImGui.SetNextItemWidth(-1);
                            if (ImGui.InputFloat("Power##" + channel.ToString(), ref power, 0f, 0f, calibrated ? "%.1f" : "%.1f%%"))
                            {
                                power = Math.Max(minPower, Math.Min(maxPower, power));
                                duty = (float)lightSource.PowerToDutyCycle.Interpolate(power / scale);
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
                                var updated = new Dictionary<FipCameraSource, CalibratedLightSource>(current);
                                updated[channel] = new CalibratedLightSource(
                                    lightSource.LightSource, lightSource.DutyCycleToPower, lightSource.PowerToDutyCycle, edit.Duty);
                                current = updated;
                                edits.Remove(channel);
                                observer.OnNext(updated);
                                break;
                            }
                        }

                        ImGui.EndTable();
                    }

                    ImGui.PopFont();
                },
                observer.OnError,
                observer.OnCompleted));

            return frameSub;
        });
    }
}

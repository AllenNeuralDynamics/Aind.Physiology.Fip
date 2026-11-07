using System;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Hexa.NET.ImGui;

/// <summary>
/// Represents an operator that renders the photobleaching control: the time spent bleaching and
/// a Stop button. Emits a notification when Stop is pressed. Source1 = Frame subject.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the photobleaching control: 'Time bleaching: HH:MM:SS', the Details text, the scheduled stop time and a Stop button. Emits a notification when Stop is pressed. StartTime and ScheduledStopTime are set via PropertyMapping. Source1 = Frame subject.")]
public class PhotobleachingVisualizer
{
    static readonly Vector4 StopColor = new Vector4(0.80f, 0.15f, 0.15f, 1f);
    static readonly Vector4 StopHoveredColor = new Vector4(0.90f, 0.25f, 0.25f, 1f);
    static readonly Vector4 FlashGrayColor = new Vector4(0.40f, 0.40f, 0.40f, 1f);
    static readonly Vector4 FlashGrayHoveredColor = new Vector4(0.50f, 0.50f, 0.50f, 1f);
    static readonly Vector4 DimTextColor = new Vector4(0.70f, 0.70f, 0.70f, 1f);

    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    /// <summary>
    /// Gets or sets when the light sources were turned on. Left at its default value while they
    /// are being configured. Not serialized: assign via PropertyMapping.
    /// </summary>
    [XmlIgnore]
    public DateTimeOffset StartTime { get; set; }

    private string details = string.Empty;
    [Description("Text shown under the clock, e.g. which light source is on.")]
    public string Details { get { return details; } set { details = value; } }

    /// <summary>
    /// Gets or sets when the workflow stops on its own. Left at its default value until the stop
    /// is scheduled. Not serialized: assign via PropertyMapping.
    /// </summary>
    [XmlIgnore]
    public DateTimeOffset ScheduledStopTime { get; set; }

    public IObservable<Unit> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<Unit>(observer =>
        {
            var stopping = false;
            return frames.SubscribeSafe(Observer.Create<TTickSource>(
                _ =>
                {
                    // Mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0.
                    unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }
                    if (!Visible) return;

                    // The scheduled stop itself is sent by the workflow; here it only shows as stopping.
                    var scheduledStop = ScheduledStopTime;
                    var stopReached = scheduledStop != default(DateTimeOffset) && DateTimeOffset.Now >= scheduledStop;
                    if (Draw(StartTime, Details, scheduledStop, stopping || stopReached) && !stopping)
                    {
                        stopping = true;
                        observer.OnNext(Unit.Default);
                    }
                },
                observer.OnError,
                observer.OnCompleted));
        });
    }

    // Returns true when Stop is pressed.
    static bool Draw(DateTimeOffset startTime, string details, DateTimeOffset scheduledStop, bool stopping)
    {
        var avail = ImGui.GetContentRegionAvail();
        // Clock line is ~24 characters wide; the window holds the clock, two status lines and a button.
        var fontSize = Math.Max(14f, Math.Min(72f, Math.Min(avail.X / 16f, avail.Y / 7f)));

        string clock;
        if (stopping) clock = "Stopping...";
        else if (startTime != default(DateTimeOffset)) clock = "Time bleaching: " + FormatElapsed(DateTimeOffset.Now - startTime);
        else clock = "Turning light sources on...";

        FipPowerUi.PushFont(fontSize);
        CenteredLine(clock, null);
        ImGui.PopFont();

        FipPowerUi.PushFont(fontSize * 0.5f);
        if (!string.IsNullOrEmpty(details)) CenteredLine(details, DimTextColor);
        if (scheduledStop != default(DateTimeOffset))
        {
            CenteredLine("Scheduled to stop at " + FormatStopTime(scheduledStop), DimTextColor);
        }
        ImGui.PopFont();

        ImGui.Dummy(new Vector2(0, fontSize * 0.3f));
        var remaining = ImGui.GetContentRegionAvail();
        var buttonSize = new Vector2(remaining.X, Math.Max(fontSize * 1.5f, remaining.Y));

        // While bleaching, flash at 1 Hz (red for half a second, then gray) so it is visible from
        // across the room that the light is on. Steady red while turning on or stopping.
        var bleaching = !stopping && startTime != default(DateTimeOffset);
        var gray = bleaching && (DateTimeOffset.Now - startTime).TotalMilliseconds % 1000 >= 500;
        FipPowerUi.PushFont(fontSize);
        ImGui.PushStyleColor(ImGuiCol.Button, gray ? FlashGrayColor : StopColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, gray ? FlashGrayHoveredColor : StopHoveredColor);
        ImGui.BeginDisabled(stopping);
        var pressed = ImGui.Button("Stop##Photobleaching", buttonSize);
        ImGui.EndDisabled();
        ImGui.PopStyleColor(2);
        ImGui.PopFont();
        return pressed && !stopping;
    }

    static void CenteredLine(string text, Vector4? color)
    {
        var width = ImGui.CalcTextSize(text).X;
        var offset = (ImGui.GetContentRegionAvail().X - width) * 0.5f;
        if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        if (color.HasValue) ImGui.TextColored(color.Value, text);
        else ImGui.TextUnformatted(text);
    }

    // Hours keep counting past a day, since a bleach can run for many hours.
    static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return ((int)elapsed.TotalHours).ToString("00") + ":" + elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00");
    }

    // e.g. "08:00 (Sat 08 Nov)", in local time.
    static string FormatStopTime(DateTimeOffset stop)
    {
        var local = stop.ToLocalTime();
        return local.ToString("HH:mm", CultureInfo.InvariantCulture) + " (" + local.ToString("ddd dd MMM", CultureInfo.InvariantCulture) + ")";
    }

}

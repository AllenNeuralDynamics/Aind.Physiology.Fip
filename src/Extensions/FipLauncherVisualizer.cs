using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Reactive.Linq;
using Bonsai;
using Hexa.NET.ImGui;

/// <summary>What a <see cref="FipLauncherCommand"/> asks for.</summary>
public enum FipLauncherCommandKind
{
    StartExperiment,
    EndExperiment,
    Annotation,
}

/// <summary>
/// A button press or a sent note from <see cref="FipLauncherVisualizer"/>. <see cref="Text"/> is
/// the note for <see cref="FipLauncherCommandKind.Annotation"/>, and null otherwise.
/// </summary>
public class FipLauncherCommand
{
    public FipLauncherCommand(FipLauncherCommandKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public FipLauncherCommandKind Kind { get; private set; }

    public string Text { get; private set; }

    public override string ToString()
    {
        return Text == null ? Kind.ToString() : Kind.ToString() + ": " + Text;
    }
}

/// <summary>
/// Represents an operator that renders the experiment launcher: Start / End Experiment buttons
/// on top, and below them a note box with a Send button and the list of notes sent so far.
/// Emits a <see cref="FipLauncherCommand"/> for each press or sent note; split them with
/// <see cref="FipLauncherCommandOfKind"/>.
/// </summary>
/// <remarks>
/// Notes are sent with Send or Enter (Ctrl+Enter adds a new line); empty notes are not sent, and
/// the box is cleared after sending. Source1 = Frame subject.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Renders the experiment launcher: Start / End Experiment buttons, a note box with Send, and the notes sent so far. Emits a FipLauncherCommand per press or sent note (split with FipLauncherCommandOfKind). Source1 = Frame subject.")]
public class FipLauncherVisualizer
{
    private bool visible = true;
    [Description("Specifies whether the control is displayed.")]
    public bool Visible { get { return visible; } set { visible = value; } }

    private volatile bool startEnabled = true;
    [Description("Specifies whether Start Experiment can be pressed. Map it from the negated experiment state.")]
    public bool StartEnabled { get { return startEnabled; } set { startEnabled = value; } }

    // Start / End button font, in 96-DPI pixels.
    const float buttonFontSize = 16f;
    const int maxNoteLength = 4096;
    // Same blue as the ROI manager's toggles; End in a slate grey.
    static readonly Vector4 StartColor = new Vector4(0.20f, 0.45f, 0.85f, 1f);
    static readonly Vector4 EndColor = new Vector4(0.40f, 0.45f, 0.52f, 1f);

    private class SentNote
    {
        public DateTime Time;
        public string Text;
    }

    public IObservable<FipLauncherCommand> Process<TTickSource>(IObservable<TTickSource> frames)
    {
        return Observable.Create<FipLauncherCommand>(observer =>
        {
            var note = string.Empty;
            var refocusNote = false;
            var history = new List<SentNote>();
            var scrollHistory = false;

            return frames.Subscribe(_ =>
            {
                // Mirrors bonsai-rx/imgui PR #29, not yet in 0.1.0.
                unsafe { ImGui.GetIO().Handle->ConfigErrorRecoveryEnableAssert = 0; }
                FipStyle.Apply();
                if (!Visible) return;

                var spacing = FipDpi.ItemSpacing;
                var avail = ImGui.GetContentRegionAvail();

                // Start / End Experiment, side by side.
                var buttonHeight = Math.Max(ImGui.GetFrameHeight() * 1.6f, Math.Min(FipDpi.Px(44f), avail.Y * 0.35f));
                var buttonSize = new Vector2((avail.X - spacing.X) / 2f, buttonHeight);
                FipPowerUi.PushFont(FipDpi.Px(buttonFontSize));
                var canStart = StartEnabled;
                ImGui.BeginDisabled(!canStart);
                PushButtonColor(StartColor);
                if (ImGui.Button((canStart ? "Start Experiment" : "Experiment Running") + "##FipLauncherStart", buttonSize))
                {
                    observer.OnNext(new FipLauncherCommand(FipLauncherCommandKind.StartExperiment, null));
                }
                ImGui.PopStyleColor(4);
                ImGui.EndDisabled();
                ImGui.SameLine();
                PushButtonColor(EndColor);
                if (ImGui.Button("End Experiment##FipLauncherEnd", buttonSize))
                {
                    observer.OnNext(new FipLauncherCommand(FipLauncherCommandKind.EndExperiment, null));
                }
                ImGui.PopStyleColor(4);
                ImGui.PopFont();

                // Notes: input box and Send on the left, the notes already sent on the right.
                var notesHeight = Math.Max(ImGui.GetFrameHeight(), ImGui.GetContentRegionAvail().Y);
                var width = ImGui.GetContentRegionAvail().X;
                var sendWidth = FipDpi.FitWidth("Send", 60f);
                var historyWidth = width * 0.4f;
                var inputWidth = Math.Max(FipDpi.Px(40f), width - historyWidth - sendWidth - 2f * spacing.X);

                if (refocusNote)
                {
                    ImGui.SetKeyboardFocusHere();
                    refocusNote = false;
                }
                var send = ImGui.InputTextMultiline("##FipLauncherNote", ref note, new UIntPtr((uint)maxNoteLength),
                    new Vector2(inputWidth, notesHeight),
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CtrlEnterForNewLine);
                // Placeholder while the box is empty and not selected; multiline boxes have no
                // built-in hint (InputTextWithHint is single-line only), so draw it over the box.
                if (note.Length == 0 && !ImGui.IsItemActive())
                {
                    ImGui.GetWindowDrawList().AddText(
                        ImGui.GetItemRectMin() + FipDpi.FramePadding,
                        ImGui.GetColorU32(ImGuiCol.TextDisabled),
                        "Enter text here");
                }
                ImGui.SetItemTooltip("Note for this session. Enter sends it, Ctrl+Enter adds a new line.");
                ImGui.SameLine();
                send |= ImGui.Button("Send##FipLauncherSend", new Vector2(sendWidth, notesHeight));

                if (send && note != string.Empty)
                {
                    observer.OnNext(new FipLauncherCommand(FipLauncherCommandKind.Annotation, note));
                    history.Add(new SentNote { Time = DateTime.Now, Text = note });
                    scrollHistory = true;
                    note = string.Empty;
                    // An active text box keeps showing its own buffer; release it so the cleared
                    // note shows, then give it the focus back on the next frame.
                    ImGuiP.ClearActiveID();
                    refocusNote = true;
                }

                ImGui.SameLine();
                ImGui.BeginChild("##FipLauncherHistory", new Vector2(0f, notesHeight), ImGuiChildFlags.Borders);
                if (history.Count == 0)
                {
                    ImGui.TextDisabled("Sent notes appear here.");
                }
                foreach (var sent in history)
                {
                    ImGui.TextDisabled(sent.Time.ToString("HH:mm:ss"));
                    ImGui.SameLine();
                    ImGui.TextWrapped(sent.Text);
                }
                if (scrollHistory)
                {
                    ImGui.SetScrollHereY(1f);
                    scrollHistory = false;
                }
                ImGui.EndChild();
            },
            observer.OnError,
            observer.OnCompleted);
        });
    }

    static void PushButtonColor(Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Math.Min(1f, color.X + 0.08f), Math.Min(1f, color.Y + 0.08f), Math.Min(1f, color.Z + 0.08f), 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.X * 0.85f, color.Y * 0.85f, color.Z * 0.85f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }
}

/// <summary>
/// Represents an operator that keeps only the <see cref="FipLauncherCommand"/> values of one kind.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Keeps only the FipLauncherCommand values of the given kind.")]
public class FipLauncherCommandOfKind
{
    [Description("The kind of command to keep.")]
    public FipLauncherCommandKind Kind { get; set; }

    public IObservable<FipLauncherCommand> Process(IObservable<FipLauncherCommand> source)
    {
        return source.Where(command => command.Kind == Kind);
    }
}

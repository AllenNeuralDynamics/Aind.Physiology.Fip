using System;
using System.ComponentModel;
using System.Reactive.Linq;
using Bonsai;
using Hexa.NET.ImGui;

/// <summary>
/// Represents an operator that scales all ImGui text in the current context, including the
/// tab bar the host draws around the visualizers. Pass-through: every input is emitted unchanged
/// after the scale is applied, so it can be placed inline between the tab tick and the visualizers.
/// </summary>
/// <remarks>
/// The tab bar is drawn by the host before this tick runs, so a change takes effect on the next
/// frame. The scale is global to the ImGui context, not just this tab. The Fip visualizers divide
/// their own font size by this scale (see <see cref="FipPowerUi.PushFont"/>), so their content
/// keeps its auto-fit size and only the tab bar and other unscaled text grow.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Scales the ImGui font of the whole context, including the tab labels drawn by the host. Pass-through: place it after the tab tick (frame subject). Takes effect on the next frame.")]
public class FipTabScale
{
    private float scale = 1.5f;
    [Description("Font scale applied to the ImGui style. 1 is the default size.")]
    public float Scale { get { return scale; } set { scale = value; } }

    public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
    {
        return source.Select(value =>
        {
            var scale = Math.Max(0.25f, Scale);
            // ImGuiStylePtr.FontScaleMain isn't supported by Bonsai's script-extensions compiler
            // (CS0570); go through the underlying struct instead.
            unsafe { ImGui.GetStyle().Handle->FontScaleMain = scale; }
            return value;
        });
    }
}

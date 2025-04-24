using System;
using Bonsai.Vision.Design;
using Bonsai;
using Bonsai.Dag;
using OpenCV.Net;
using Bonsai.Vision;
using System.Reactive.Linq;
using Bonsai.Design;
using Bonsai.Expressions;
using System.Linq;
using System.Windows.Forms;

[assembly: TypeVisualizer(typeof(SelectCirclesVisualizer), Target = typeof(SelectCircles))]

public class SelectCirclesVisualizer : DialogTypeVisualizer
{
    ImageEllipsePicker ellipsePicker;
    IDisposable inputHandle;

    /// <inheritdoc/>
    public override void Show(object value)
    {
    }

    /// <inheritdoc/>
    public override void Load(IServiceProvider provider)
    {
        var context = (ITypeVisualizerContext)provider.GetService(typeof(ITypeVisualizerContext));
        var visualizerElement = ExpressionBuilder.GetVisualizerElement(context.Source);
        var selectRegions = (SelectCircles)ExpressionBuilder.GetWorkflowElement(visualizerElement.Builder);

        ellipsePicker = new ImageEllipsePicker { IsCirclePicker = true, LabelRegions = true, Dock = DockStyle.Fill };
        UpdateRegions(selectRegions);

        selectRegions.RefreshRequested += () => UpdateRegions(selectRegions);

        ellipsePicker.RegionsChanged += delegate
        {
            selectRegions.Circles = ellipsePicker.Regions.ToArray()
                .Select(region => new Circle(region.Center, region.Size.Width / 2))
                .ToArray();
        };

        var imageInput = selectRegions.imageStream;
        if (imageInput != null)
        {
            inputHandle = imageInput.Subscribe(value => ellipsePicker.Image = (IplImage)value);
            ellipsePicker.HandleDestroyed += delegate { inputHandle.Dispose(); };
        }

        var visualizerService = (IDialogTypeVisualizerService)provider.GetService(typeof(IDialogTypeVisualizerService));
        if (visualizerService != null)
        {
            visualizerService.AddControl(ellipsePicker);
        }
    }

    private void UpdateRegions(SelectCircles selectRegions)
    {
        if (selectRegions == null || ellipsePicker == null) return;
        ellipsePicker.Regions.Clear();
        foreach (var circle in selectRegions.Circles)
        {
            var region = new RotatedRect(circle.Center, new Size2f(circle.Radius * 2, circle.Radius * 2), 0);
            ellipsePicker.Regions.Add(region);
        }
    }

    /// <inheritdoc/>
    public override void Unload()
    {
        if (ellipsePicker != null)
        {
            ellipsePicker.Dispose();
            ellipsePicker = null;
        }
    }
}

static class VisualizerHelper
    {
        internal static IObservable<object> ImageInput(IServiceProvider provider)
        {
            InspectBuilder inspectBuilder = null;
            ExpressionBuilderGraph workflow = (ExpressionBuilderGraph)provider.GetService(typeof(ExpressionBuilderGraph));
            ITypeVisualizerContext context = (ITypeVisualizerContext)provider.GetService(typeof(ITypeVisualizerContext));
            if (workflow != null && context != null)
            {
                inspectBuilder = (from node in workflow
                                where node.Value == context.Source
                                select (from p in workflow.Predecessors(node)
                                        select p.Value as InspectBuilder).FirstOrDefault()).FirstOrDefault();
            }

            if (inspectBuilder != null && inspectBuilder.ObservableType == typeof(IplImage))
            {
                return inspectBuilder.Output.Merge();
            }

            return null;
        }
    }
using Bonsai;
using Bonsai.Dag;
using Bonsai.Design;
using Bonsai.Expressions;
using Bonsai.Vision.Design;
using OpenCV.Net;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Drawing;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Forms;
using Font = OpenCV.Net.Font;
using Point = OpenCV.Net.Point;
using Size = OpenCV.Net.Size;
[assembly: TypeVisualizer(typeof(CircleActivityCollectionVisualizer), Target = typeof(CircleActivityCollection))]

namespace Bonsai.Vision.Design
{
    /// <summary>
    /// Provides a type visualizer that displays a collection of polygonal regions
    /// of interest and their activity measurements.
    /// </summary>
    public class CircleActivityCollectionVisualizer : IplImageVisualizer
    {
        const int RoiThickness = 1;
        const float DefaultDpiWidth = 6;
        static readonly Scalar InactiveRoi = Scalar.Rgb(255, 0, 0);
        static readonly Scalar ActiveRoi = Scalar.Rgb(0, 255, 0);

        Font font;
        IplImage input;
        IplImage canvas;
        IDisposable inputHandle;
        CircleActivityCollection regions;

        /// <inheritdoc/>
        public override void Show(object value)
        {
            regions = (CircleActivityCollection)value;
            if (input != null)
            {
                canvas = IplImageHelper.EnsureColorCopy(canvas, input);
                for (int i = 0; i < regions.Count; i++)
                {
                    var circle = regions[i].Circle;
                    var color = regions[i].Activity.Val0 > 0 ? ActiveRoi : InactiveRoi;

                    var label = i.ToString();
                    var activity = regions[i].Activity.Val0.ToString("0.##");
                    Size textSize;
                    int baseline;

                    CV.GetTextSize(label, font, out textSize, out baseline);
                    var text = label + ": " + activity;
                    CV.PutText(canvas, text, new Point((int)(circle.Center.X - circle.Radius), (int)(circle.Center.Y - circle.Radius)), font, color);
                }

                base.Show(canvas);
            }
        }

        public static Vector2 NormalizePoint(Point point, Size imageSize)
        {
            return new Vector2((float)point.X * 2f / (float)imageSize.Width - 1f, 0f - ((float)point.Y * 2f / (float)imageSize.Height - 1f));
        }

        /// <inheritdoc/>
        protected override void RenderFrame()
        {
            GL.Color3(Color.White);
            base.RenderFrame();

            if (regions != null)
            {
                GL.Disable(EnableCap.Texture2D);
                foreach (var region in regions)
                {
                    var color = region.Activity.Val0 > 0 ? Color.LimeGreen : Color.Red;
                    GL.Color3(color);
                    GL.Begin(PrimitiveType.LineLoop);

                    var circle = region.Circle;
                    const int segments = 100;
                    for (int i = 0; i < segments; i++)
                    {
                        var angle = 2 * Math.PI * i / segments;
                        var x = circle.Center.X + circle.Radius * Math.Cos(angle);
                        var y = circle.Center.Y + circle.Radius * Math.Sin(angle);
                        GL.Vertex2(NormalizePoint(new Point((int)x, (int)y), input.Size));
                    }
                    GL.End();
                }
            }
        }
        public override void Load(IServiceProvider provider)
        {
            IObservable<object> observable = VisualizerHelper.ImageInput(provider);
            if (observable != null)
            {
                inputHandle = observable.Subscribe(delegate (object value)
                {
                    input = (IplImage)value;
                });
            }

            base.Load(provider);
            font = new OpenCV.Net.Font(1.0);
            ((UserControl)VisualizerCanvas).Load += delegate
            {
                float num = ((ContainerControl)VisualizerCanvas).AutoScaleDimensions.Width / 6f;
                GL.LineWidth(1f * num);
            };
    }

        public override void Unload()
        {
            if (canvas != null)
            {
                canvas.Close();
                canvas = null;
            }

            if (inputHandle != null)
            {
                inputHandle.Dispose();
                inputHandle = null;
            }

            base.Unload();
        }
    }
}

internal static class VisualizerHelper
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
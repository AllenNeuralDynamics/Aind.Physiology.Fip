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

    public class CircleActivityCollectionVisualizer : IplImageVisualizer
    {
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
                    var c = GetColor(i);
                    var color = new Scalar(c.B, c.G, c.R);
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


        private static readonly Color[] ColorPalette = new Color[]
        {
            Color.Red, Color.Green, Color.Blue, Color.Yellow,
            Color.Cyan, Color.Magenta, Color.Orange, Color.Purple
        };

        private static Color GetColor(int index)
        {
            return ColorPalette[index % ColorPalette.Length];
        }
    
        public static Vector2 NormalizePoint(Point point, Size imageSize)
        {
            return new Vector2((float)point.X * 2f / (float)imageSize.Width - 1f, 0f - ((float)point.Y * 2f / (float)imageSize.Height - 1f));
        }

        protected override void RenderFrame()
        {
            GL.Color3(Color.White);
            base.RenderFrame();

            if (regions != null)
            {
                GL.Disable(EnableCap.Texture2D);
                for (int j=0; j < regions.Count; j++)
                {
                    var region = regions[j];
                    var color = GetColor(j);
                    GL.Color3(color);
                    GL.LineWidth(4);
                    GL.Begin(PrimitiveType.LineLoop);
                    var roi = region.AsPolygon();
                    for (int i = 0; i < roi.Length; i++)
                    {
                        GL.Vertex2(NormalizePoint(roi[i], input.Size));
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
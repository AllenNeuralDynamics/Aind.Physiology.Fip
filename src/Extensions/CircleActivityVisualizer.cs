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
using FipExtensions;

[assembly: TypeVisualizer(typeof(CircleActivityCollectionVisualizer), Target = typeof(CircleActivityCollection))]
[assembly: TypeVisualizer(typeof(CircleActivityCollectionVisualizer), Target = typeof(Bonsai.Harp.Timestamped<CircleActivityCollection>))]

namespace Bonsai.Vision.Design
{

    public class CircleActivityCollectionVisualizer : IplImageVisualizer
    {
        Font font;
        IplImage input;
        IplImage canvas;
        IDisposable inputHandle;
        CircleActivityCollection regions;

        /// <inheritdoc/>
        public override void Show(object value)
        {

            if (value is Bonsai.Harp.Timestamped<CircleActivityCollection>)
            {
                regions = ((Bonsai.Harp.Timestamped<CircleActivityCollection>)value).Value;
            }
            else
            {
                regions = (CircleActivityCollection)value;
            }
            input = regions.FipFrame.Image;
            if (input != null)
            {
                // Not sure if it may be a good or bad idea to normalize by maximum.
                canvas = IplImageHelper.EnsureColorCopy(canvas, input);
                double min, max;
                CV.MinMaxLoc(input, out min, out max);
                CV.ConvertScale(canvas, canvas, (double)UInt16.MaxValue / max);
                int colorGain;
                switch (canvas.Depth)
                {
                    case IplDepth.U8:
                    case IplDepth.S8:
                        colorGain = 1;
                        break;
                    case IplDepth.U16:
                    case IplDepth.S16:
                        colorGain = 255;
                        break;
                    default:
                        colorGain = 1;
                        break;
                }
                for (int i = 0; i < regions.Count; i++)
                {
                    var circle = regions[i].Circle;
                    Size textSize;
                    int baseline;
                    var label = i.ToString();
                    CV.GetTextSize(label, font, out textSize, out baseline);
                    var color = GetColor(i);
                    var scalarColor = new Scalar(color.B * colorGain, color.G * colorGain, color.R * colorGain);
                    CV.PutText(canvas, label, new Point(circle.Center) - new Point(textSize.Width / 2, -textSize.Height / 2), font, scalarColor);
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

        public static Vector2 NormalizePoint(Point2f point, Size imageSize)
        {
            return new Vector2(point.X * 2f / (float)imageSize.Width - 1f, 0f - (point.Y * 2f / (float)imageSize.Height - 1f));
        }
        protected override void RenderFrame()
        {
            GL.Color3(Color.White);
            base.RenderFrame();
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            if (regions != null)
            {
                GL.Disable(EnableCap.Texture2D);
                const int numVertices = 25;
                for (int j = 0; j < regions.Count; j++)
                {
                    var region = regions[j];
                    var color = GetColor(j);
                    var roi = region.Circle;

                    // Precompute circle vertices
                    var vertices = new Vector2[numVertices];
                    for (int i = 0; i < numVertices; i++)
                    {
                        float angle = (float)(i * Math.PI * 2 / numVertices);
                        float x = roi.Center.X + roi.Radius * (float)Math.Cos(angle);
                        float y = roi.Center.Y + roi.Radius * (float)Math.Sin(angle);
                        vertices[i] = NormalizePoint(new Point2f(x, y), input.Size);
                    }

                    // Fill the polygon
                    GL.Color4(color.R, color.G, color.B, 0.1f);
                    GL.Begin(PrimitiveType.Polygon);
                    foreach (var vertex in vertices)
                    {
                        GL.Vertex2(vertex);
                    }
                    GL.End();

                    // Draw the outline
                    GL.Color4(color.R, color.G, color.B, 1.0f);
                    GL.LineWidth(2f);
                    GL.Begin(PrimitiveType.LineLoop);
                    foreach (var vertex in vertices)
                    {
                        GL.Vertex2(vertex);
                    }
                    GL.End();
                }
            }
            GL.Disable(EnableCap.Blend);
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


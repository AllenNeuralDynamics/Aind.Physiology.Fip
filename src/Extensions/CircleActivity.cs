using OpenCV.Net;
using Bonsai.Vision;
using System;

namespace FipExtensions
{
    public class CircleActivity
    {
        // The ROI definition (Background or Roi) this activity was computed from, including its
        // RoiNumber, FiberNumber, Name and Enabled metadata.
        public AindPhysiologyFip.FipCircle FipCircle { get; set; }
        public Scalar Activity { get; set; }

        public CircleActivity() { }

        public override string ToString()
        {
            return string.Format("FipCircle: {0}, Activity: {1}", FipCircle, Activity);
        }


        public ConnectedComponent AsConnectedComponent()
        {
            return ConnectedComponent.FromContour(SeqFromArray(AsPolygon()));
        }

        public Point[] AsPolygon(int nSegments = 30)
        {
            var polygon = new Point[nSegments];
            for (int i = 0; i < nSegments; i++)
            {
                var angle = 2 * Math.PI * i / nSegments;
                var x = FipCircle.Center.X + FipCircle.Radius * Math.Cos(angle);
                var y = FipCircle.Center.Y + FipCircle.Radius * Math.Sin(angle);
                polygon[i] = new Point((int)x, (int)y);
            }
            return polygon;
        }

        private static Seq SeqFromArray(Point[] input)
        {
            var storage = new MemStorage();
            var output = new Seq(Depth.S32, 2, SequenceKind.Curve, storage);
            if (input.Length > 0)
            {
                output.Push(input);
            }
            return output;
        }
    }
}
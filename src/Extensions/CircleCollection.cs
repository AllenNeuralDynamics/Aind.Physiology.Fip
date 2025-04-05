using Bonsai;
using System;
using System.ComponentModel;
using OpenCV.Net;
using Bonsai.Vision;

public class CircleCollection
{

    private Circle[] circles = new Circle[] { };
    [Editor("Bonsai.Vision.Design.IplImageCircleEditor, Bonsai.Vision.Design", DesignTypes.UITypeEditor)]
    public Circle[] Circles
    {
        get { return circles; }
        set { circles = value; }
    }

    public IplImage Image { get; set; }

    public ConnectedComponentCollection ConnectedComponents
    {
        get { return CalculateConnectedComponents(); }
    }


    public CircleCollection(IplImage image)
    {
        Image = image;
    }

    private readonly int n = 30;
    private ConnectedComponentCollection CalculateConnectedComponents()
    {
        if (Image == null)
        {
            throw new InvalidOperationException("Image is null. Cannot calculate connected components.");
        }
        ;
        var rois = new ConnectedComponentCollection(Image.Size);
        foreach (var circle in Circles)
        {
            var polygon = new Point[n];
            for (int i = 0; i < n; i++)
            {
                var angle = 2 * Math.PI * i / n;
                var x = circle.Center.X + circle.Radius * Math.Cos(angle);
                var y = circle.Center.Y + circle.Radius * Math.Sin(angle);
                polygon[i] = new Point((int)x, (int)y);

            }
            var component = ConnectedComponent.FromContour(SeqFromArray(polygon));
            rois.Add(component);
        }
        return rois;
    }

    private Seq SeqFromArray(Point[] input)
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




using System.Collections.ObjectModel;
using OpenCV.Net;
using Bonsai.Vision;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bonsai;

[DefaultProperty("Circles")]
[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Calculates activation intensity inside specified regions of interest for each image in the sequence.")]
public class CircleActivityCalculator
{

    [Description("The regions of interest for which to calculate activation intensity.")]
    [Editor("Bonsai.Vision.Design.IplImageCircleEditor, Bonsai.Vision.Design", DesignTypes.UITypeEditor)]
    public Circle[] Circles { get; set; }

    private ReduceOperation operation = ReduceOperation.Sum;
    [Description("Specifies the reduction operation used to calculate activation intensity.")]
    public ReduceOperation Operation
    {
        get { return operation; }
        set { operation = value; }
    }

    public IObservable<CircleActivityCollection> Process(IObservable<IplImage> source)
    {
        return Observable.Defer(() =>
        {
            var roi = default(IplImage);
            var mask = default(IplImage);
            var currentCircles = default(Circle[]);
            var boundingRegions = default(Rect[]);
            return source.Select(img =>
            {
                var operation = Operation;
                var output = new CircleActivityCollection(img);
                mask = IplImageHelper.EnsureImageFormat(mask, img.Size, IplDepth.U8, 1);
                if (operation != ReduceOperation.Sum) roi = null;
                else roi = IplImageHelper.EnsureImageFormat(roi, img.Size, img.Depth, img.Channels);
                if (Circles != currentCircles)
                {
                    currentCircles = Circles;
                    if (currentCircles != null)
                    {
                        mask.SetZero();
                        foreach (var circle in currentCircles)
                        {
                            CV.Circle(mask, new Point((int)circle.Center.X, (int)circle.Center.Y), (int)circle.Radius, Scalar.All(255), -1);
                        }

                        boundingRegions = currentCircles.Select(circle =>
                        {
                            var left = (int)(circle.Center.X - circle.Radius);
                            var top = (int)(circle.Center.Y - circle.Radius);
                            return new Rect(left, top, (int)circle.Radius*2, (int)circle.Radius*2);
                        }).ToArray();
                    }
                }

                if (currentCircles != null)
                {
                    var activeMask = mask;
                    if (roi != null)
                    {
                        roi.SetZero();
                        CV.Copy(img, roi, mask);
                        activeMask = roi;
                    }

                    var activation = ActivationFunction(operation);
                    for (int i = 0; i < boundingRegions.Length; i++)
                    {
                        var rect = boundingRegions[i];
                        var circle = currentCircles[i];
                        using (var region = img.GetSubRect(rect))
                        using (var regionMask = activeMask.GetSubRect(rect))
                        {
                            output.Add(new CircleActivity
                            {
                                Image = img,
                                Circle = circle,
                                Activity = activation(region, regionMask)
                            });
                        }
                    }
                }

                return output;
            });
        });
    }

    static Func<IplImage, IplImage, Scalar> ActivationFunction(ReduceOperation operation)
    {
        switch (operation)
        {
            case ReduceOperation.Avg: return CV.Avg;
            case ReduceOperation.Max:
                return (image, mask) =>
            {
                Scalar min, max;
                MinMaxLoc(image, mask, out min, out max);
                return max;
            };
            case ReduceOperation.Min:
                return (image, mask) =>
            {
                Scalar min, max;
                MinMaxLoc(image, mask, out min, out max);
                return min;
            };
            case ReduceOperation.Sum: return (image, mask) => CV.Sum(mask);
            default: throw new InvalidOperationException("The specified reduction operation is invalid.");
        }
    }

    static void MinMaxLoc(IplImage image, IplImage mask, out Scalar min, out Scalar max)
    {
        Point minLoc, maxLoc;
        if (image.Channels == 1)
        {
            CV.MinMaxLoc(image, out min.Val0, out max.Val0, out minLoc, out maxLoc, mask);
            min.Val1 = max.Val1 = min.Val2 = max.Val2 = min.Val3 = max.Val3 = 0;
        }
        else
        {
            using (var coi = image.GetSubRect(new Rect(0, 0, image.Width, image.Height)))
            {
                coi.ChannelOfInterest = 1;
                CV.MinMaxLoc(coi, out min.Val0, out max.Val0, out minLoc, out maxLoc, mask);
                coi.ChannelOfInterest = 2;
                CV.MinMaxLoc(coi, out min.Val1, out max.Val1, out minLoc, out maxLoc, mask);
                if (image.Channels > 2)
                {
                    coi.ChannelOfInterest = 3;
                    CV.MinMaxLoc(coi, out min.Val2, out max.Val2, out minLoc, out maxLoc, mask);
                    if (image.Channels > 3)
                    {
                        coi.ChannelOfInterest = 4;
                        CV.MinMaxLoc(coi, out min.Val3, out max.Val3, out minLoc, out maxLoc, mask);
                    }
                    else min.Val3 = max.Val3 = 0;
                }
                else min.Val2 = max.Val2 = min.Val3 = max.Val3 = 0;
            }
        }
    }
}


public class CircleActivityCollection : Collection<CircleActivity>
{
    public IplImage Image { get; set; }
    public CircleActivityCollection(IplImage image) : base()
    {
        Image = image;
    }
}

public class CircleActivity
{
    public Circle Circle { get; set; }
    public Scalar Activity { get; set; }
    public IplImage Image { get; set; }


    public CircleActivity(){}
    public CircleActivity(IplImage image)
    {
        Image = image;
    }

    public override string ToString()
    {
        return string.Format("Circle: {0}, Activity: {1}", Circle, Activity);
    }


    public ConnectedComponent AsConnectedComponent(){
        return ConnectedComponent.FromContour(SeqFromArray(AsPolygon()));
    }

    public Point[] AsPolygon(int nSegments = 30){
        var polygon = new Point[nSegments];
        for (int i = 0; i < nSegments; i++)
        {
            var angle = 2 * Math.PI * i / nSegments;
            var x = Circle.Center.X + Circle.Radius * Math.Cos(angle);
            var y = Circle.Center.Y + Circle.Radius * Math.Sin(angle);
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

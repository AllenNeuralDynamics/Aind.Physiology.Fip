using OpenCV.Net;
using Bonsai.Vision;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.ComponentModel;
using Bonsai;
using Bonsai.Harp;
using AindPhysiologyFip;

namespace FipExtensions
{
    [DefaultProperty("RoiSettings")]
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Stamps Source onto each frame (replacing a separate ModifyFipCameraSource step) and calculates activation intensity inside the matching RoiSettings circles (Background first, then each Roi in order).")]
    public class CircleActivityCalculator
    {
        [Description("The Roi settings (Background + per-camera Rois) used to calculate activation intensity. The circles for the current Source are derived automatically: Background first, then each Roi in order.")]
        public RoiSettings RoiSettings { get; set; }

        private FipCameraSource source = FipCameraSource.None;
        [Description("The camera source of the incoming frames. Selects which RoiSettings circles apply (Iso and Green both use the GreenIso circles; Red uses the Red circles) and is stamped onto each outgoing frame's Source.")]
        public FipCameraSource Source
        {
            get { return source; }
            set { source = value; }
        }

        private ReduceOperation operation = ReduceOperation.Avg;
        [Description("Specifies the reduction operation used to calculate activation intensity.")]
        public ReduceOperation Operation
        {
            get { return operation; }
            set { operation = value; }
        }

        public IObservable<Timestamped<CircleActivityCollection>> Process(IObservable<Timestamped<FipFrame>> source){
            return Process(source.Select(frame => frame.Value))
                .Zip(source, (activity, frame) => Timestamped.Create(activity, frame.Seconds));
        }

        public IObservable<CircleActivityCollection> Process(IObservable<FipFrame> source)
        {
            return Observable.Defer(() =>
            {
                var roi = default(IplImage);
                var mask = default(IplImage);
                var currentRoiSettings = default(RoiSettings);
                var currentSource = default(FipCameraSource);
                var currentCircles = default(Bonsai.Vision.Circle[]);
                var boundingRegions = default(Rect[]);
                return source.Select(frame =>
                {
                    var cameraSource = Source;
                    frame = new FipFrame(frame) { Source = cameraSource };

                    var operation = Operation;
                    var output = new CircleActivityCollection(frame);
                    var img = frame.Image;
                    mask = IplImageHelper.EnsureImageFormat(mask, img.Size, IplDepth.U8, 1);
                    if (operation != ReduceOperation.Sum) roi = null;
                    else roi = IplImageHelper.EnsureImageFormat(roi, img.Size, img.Depth, img.Channels);

                    var roiSettings = RoiSettings;
                    if (roiSettings != currentRoiSettings || cameraSource != currentSource)
                    {
                        currentRoiSettings = roiSettings;
                        currentSource = cameraSource;
                        currentCircles = ToCircleArray(roiSettings, cameraSource);
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
                                var width = Math.Min((int)circle.Radius * 2, img.Width - left);
                                var height = Math.Min((int)circle.Radius * 2, img.Height - top);
                                left = Math.Max(left, 0);
                                top = Math.Max(top, 0);
                                return new Rect(left, top, width, height);
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

        // Background first, then each Roi in order - matching the RoiManagerVisualizer/
        // RoiCircleConverter convention. Iso and Green share the GreenIso circles (same
        // optics/sensor, just different exposures); Red has its own.
        private static Bonsai.Vision.Circle[] ToCircleArray(RoiSettings settings, FipCameraSource cameraSource)
        {
            if (settings == null) return null;

            AindPhysiologyFip.Circle background;
            List<AindPhysiologyFip.Circle> rois;
            switch (cameraSource)
            {
                case FipCameraSource.Iso:
                case FipCameraSource.Green:
                    background = settings.CameraGreenIsoBackground;
                    rois = settings.CameraGreenIsoRoi;
                    break;
                case FipCameraSource.Red:
                    background = settings.CameraRedBackground;
                    rois = settings.CameraRedRoi;
                    break;
                default:
                    return null;
            }

            var ordered = new List<AindPhysiologyFip.Circle>();
            if (background != null) ordered.Add(background);
            if (rois != null) ordered.AddRange(rois);
            return ordered.Select(ConvertToBonsaiVisionCircle).ToArray();
        }

        private static Bonsai.Vision.Circle ConvertToBonsaiVisionCircle(AindPhysiologyFip.Circle circle)
        {
            return new Bonsai.Vision.Circle
            {
                Center = new OpenCV.Net.Point2f((float)circle.Center.X, (float)circle.Center.Y),
                Radius = (float)circle.Radius
            };
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
}

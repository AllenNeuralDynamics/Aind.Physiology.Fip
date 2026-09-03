using Bonsai;
using Bonsai.Spinnaker;
using OpenCV.Net;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

namespace FipExtensions
{
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Transform)]
    [Description("Converts SpinnakerDataFrame to FipFrame.")]
    public class ToFipFrame
    {
        public FipCameraSource CameraSource { get; set; }

        public IObservable<FipFrame> Process(IObservable<SpinnakerDataFrame> source)
        {
            if (source == null) throw new InvalidOperationException("Source must be set before processing.");
            return source.Select(frame => new FipFrame { Image = frame.Image, Source = CameraSource, FrameNumber = frame.ChunkData.FrameID, FrameTime = frame.ChunkData.Timestamp });
        }

        public IObservable<FipFrame> Process(IObservable<Tuple<IplImage, int, long>> source)
        {
            return source.Select(tuple => new FipFrame { Image = tuple.Item1, Source = CameraSource, FrameNumber = tuple.Item2, FrameTime = tuple.Item3 });
        }
    }
}
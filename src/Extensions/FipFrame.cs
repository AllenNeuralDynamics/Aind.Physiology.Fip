using Bonsai;
using Bonsai.Spinnaker;
using OpenCV.Net;
using System;
using System.ComponentModel;
using System.Reactive.Linq;

[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Represents a frame of fiber photometry data.")]
public class CreateFipFrame{

    public FipCameraSource CameraSource { get; set; }

    public IObservable<FipFrame> Process(IObservable<SpinnakerDataFrame> source){
        if (source == null) throw new InvalidOperationException("Source must be set before processing.");
        return source.Select(frame => new FipFrame { Image = frame.Image, Source = CameraSource, FrameNumber = frame.ChunkData.FrameID, FrameTime = frame.ChunkData.Timestamp });
    }
}

public class FipFrame{
    public IplImage Image { get; set; }
    public FipCameraSource Source { get; set; }
    public long FrameNumber { get; set; }
    public long FrameTime { get; set; }
}

public enum FipCameraSource{
    Iso = 0,
    Green = 1,
    Red = 2,
}
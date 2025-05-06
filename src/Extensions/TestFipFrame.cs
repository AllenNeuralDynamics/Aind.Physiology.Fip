using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;
using FipExtensions;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class TestFipFrame
{
    public IObservable<FipFrame> Process(IObservable<IplImage> source)
    {
        return source.Select(value => {
            return new FipFrame(){
                Image = value,
                FrameNumber = 0,
                FrameTime = 0,
                Source = FipCameraSource.None,
            };
        });
    }
}

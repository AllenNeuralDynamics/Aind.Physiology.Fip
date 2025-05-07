using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai.Spinnaker;
using OpenCV.Net;

[Combinator]
[Description("Flips the image in place.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class FlipInPlace
{

    private FlipMode? flipMode = null;

    public FlipMode? FlipMode
    {
        get { return flipMode; }
        set { flipMode = value; }
    }

    public IObservable<IplImage> Process(IObservable<IplImage> source)
    {
        return source.Select(img => {;
            if (!flipMode.HasValue) return img;
            if (img.Width != img.Height)
            {
                throw new ArgumentException("Image must be square to flip in place.");
            }
            CV.Flip(img, img, flipMode.Value);
            return img;
        });
    }

    public IObservable<SpinnakerDataFrame> Process(IObservable<SpinnakerDataFrame> source)
    {
        return Process(source.Select(value => value.Image)).Zip(source, (img, dataFrame) =>
        {
            return new SpinnakerDataFrame(img, dataFrame.ChunkData);
        });
    }
}
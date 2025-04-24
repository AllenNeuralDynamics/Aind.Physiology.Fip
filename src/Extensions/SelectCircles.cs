using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;
using Bonsai.Vision;

[Combinator]
[Description("Allows selecting a set of circles from an image.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
[DefaultProperty("Circles")]
public class SelectCircles
{
    private Circle[] circles = new Circle[0];
    public Circle[] Circles {
        get { return circles; }
        set
        {
            if (value != null && value.Length > 0)
            {
                circles = value;
            }
        }
        }

    public event Action RefreshRequested;

    private bool refresh;
    [Description("Triggers a refresh of the visualizer regions.")]
    public bool Refresh
    {
        get {return refresh;}
        set
        {
            if (value)
            {
                refresh = false; // Reset the property after triggering
                var handler = RefreshRequested;
                if (handler != null) handler.Invoke();
            }
        }
    }

    internal IObservable<IplImage> imageStream;

    public IObservable<Circle[]> Process(IObservable<IplImage> source)
    {
        imageStream = source;
        return source.Select(value => {
            return Circles;
            });
    }
}

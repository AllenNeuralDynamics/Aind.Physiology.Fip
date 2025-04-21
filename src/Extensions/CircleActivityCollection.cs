using OpenCV.Net;
using System.Collections.ObjectModel;
using System.Linq;
using Bonsai.Vision;


namespace FipExtensions
{
    public class CircleActivityCollection : Collection<CircleActivity>
    {
        public IplImage Image { get; set; }
        public CircleActivityCollection(IplImage image) : base()
        {
            Image = image;
        }

        public Circle[] Circles
        {
            get { return this.Select(circleActivity => circleActivity.Circle).ToArray(); }
        }
    }
}
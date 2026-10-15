using System.Collections.ObjectModel;
using System.Linq;


namespace FipExtensions
{
    public class CircleActivityCollection : Collection<CircleActivity>
    {
        public FipFrame FipFrame { get; set; }
        public CircleActivityCollection(FipFrame fipFrame) : base()
        {
            FipFrame = fipFrame;
        }

        public AindPhysiologyFip.FipCircle[] FipCircles
        {
            get { return this.Select(circleActivity => circleActivity.FipCircle).ToArray(); }
        }
    }
}
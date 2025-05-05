using Bonsai.Vision;

namespace Aind.Physiology.Fip.Extensions
{
    public static class CircleExtensions
    {
        public static bool Equals(this Circle circle, Circle other)
        {
            return circle.Center.X == other.Center.X &&
                   circle.Center.Y == other.Center.Y &&
                   circle.Radius == other.Radius;
        }
    }
}
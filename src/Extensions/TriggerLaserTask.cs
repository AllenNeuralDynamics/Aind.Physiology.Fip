using FipExtensions;

/// <summary>
/// Represents a request to trigger a laser task. This is a marker interface: the driving mode
/// is the concrete implementing type itself (<see cref="ContinuousLaserTask"/>,
/// <see cref="FipModeLaserTask"/>, <see cref="OffLaserTask"/>), not a separate enum/property.
/// Concrete implementations carry whatever additional settings that mode requires; use
/// <see cref="LaserTaskOfType{TResult}"/> downstream to filter/cast a mixed sequence of these
/// back down to a specific concrete type.
/// </summary>
public interface ITriggerLaserTask
{
}

/// <summary>
/// Represents a request to continuously drive a single Fip camera channel's light source at a
/// fixed duty cycle, independent of the Fip acquisition cycle.
/// </summary>
public class ContinuousLaserTask : ITriggerLaserTask
{
    /// <summary>
    /// Gets or sets the camera channel whose light source should be driven.
    /// </summary>
    public FipCameraSource Channel { get; set; }

    /// <summary>
    /// Gets or sets the duty cycle (0-1) to drive <see cref="Channel"/> at.
    /// </summary>
    public float Power { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuousLaserTask"/> class.
    /// </summary>
    public ContinuousLaserTask()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuousLaserTask"/> class with the
    /// specified channel and duty cycle.
    /// </summary>
    public ContinuousLaserTask(FipCameraSource channel, float power)
    {
        Channel = channel;
        Power = power;
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return "Continuous (" + Channel.ToString() + ", " + Power.ToString("F3") + ")";
    }
}

/// <summary>
/// Represents a request to drive light sources following the normal Fip acquisition cycle.
/// Carries no additional settings.
/// </summary>
public class FipModeLaserTask : ITriggerLaserTask
{
}

/// <summary>
/// Represents a request to stop driving all light sources. Carries no additional settings,
/// since stopping applies to every channel at once.
/// </summary>
public class OffLaserTask : ITriggerLaserTask
{
}

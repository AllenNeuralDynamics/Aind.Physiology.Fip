using FipExtensions;

/// <summary>
/// Represents a request to trigger a light source task. This is a marker interface: the driving mode
/// is the concrete implementing type itself (<see cref="ContinuousLightSourceTask"/>,
/// <see cref="FipModeLightSourceTask"/>, <see cref="OffLightSourceTask"/>), not a separate enum/property.
/// Concrete implementations carry whatever additional settings that mode requires; use
/// <see cref="LightSourceTaskOfType{TResult}"/> downstream to filter/cast a mixed sequence of these
/// back down to a specific concrete type.
/// </summary>
public interface ITriggerLightSourceTask
{
}

/// <summary>
/// Represents a request to continuously drive a single Fip camera channel's light source at a
/// fixed duty cycle, independent of the Fip acquisition cycle.
/// </summary>
public class ContinuousLightSourceTask : ITriggerLightSourceTask
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
    /// Initializes a new instance of the <see cref="ContinuousLightSourceTask"/> class.
    /// </summary>
    public ContinuousLightSourceTask()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuousLightSourceTask"/> class with the
    /// specified channel and duty cycle.
    /// </summary>
    public ContinuousLightSourceTask(FipCameraSource channel, float power)
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
public class FipModeLightSourceTask : ITriggerLightSourceTask
{
}

/// <summary>
/// Represents a request to keep the Fip acquisition cycle running (cameras still triggered) with
/// every light source at zero duty cycle, e.g. to acquire background frames. Carries no additional
/// settings, since it applies to every channel at once. Use <see cref="StopLightSourceTask"/> to stop
/// the device altogether.
/// </summary>
public class OffLightSourceTask : ITriggerLightSourceTask
{
}

/// <summary>
/// Represents a request to stop the device altogether: no light and no camera triggers. The task
/// schedule is stopped and cleared and is not restarted.
/// </summary>
public class StopLightSourceTask : ITriggerLightSourceTask
{
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using AindPhysiologyFip;
using Bonsai;
using FipExtensions;

/// <summary>
/// Sets each calibrated light source's duty cycle from the task logic's default power, so the
/// published <c>CalibratedLightSources</c> (what Fip mode and the experiment use) starts at the
/// task's values. Green = Power.Blue, Iso = Power.Uv, Red = Power.Lime, in microwatts.
/// Source1 = calibrated light sources, Source2 = task logic.
/// </summary>
/// <remarks>
/// The task powers are in microwatts, so they only apply to light sources that have a power
/// calibration. Uncalibrated light sources start at 0 rather than having microwatts clamped to full
/// duty cycle. Powers outside a calibration's range are clamped to it.
/// </remarks>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Sets each calibrated light source's duty cycle from the task logic's default power (uW). Uncalibrated light sources start at 0. Source1 = calibrated light sources, Source2 = task logic.")]
public class ApplyTaskPowers
{
    public IObservable<IDictionary<FipCameraSource, CalibratedLightSource>> Process(
        IObservable<IDictionary<FipCameraSource, CalibratedLightSource>> lightSources,
        IObservable<AindPhysioFipTaskLogic> taskLogic)
    {
        return lightSources.Zip(taskLogic, Apply);
    }

    public static IDictionary<FipCameraSource, CalibratedLightSource> Apply(
        IDictionary<FipCameraSource, CalibratedLightSource> lightSources, AindPhysioFipTaskLogic taskLogic)
    {
        var result = new Dictionary<FipCameraSource, CalibratedLightSource>();
        foreach (var pair in lightSources)
        {
            var lightSource = pair.Value;
            if (lightSource == null)
            {
                result[pair.Key] = null;
                continue;
            }

            var dutyCycle = 0.0;
            var power = TaskPower(pair.Key, taskLogic);
            if (power.HasValue && FipPowerUi.IsCalibrated(lightSource))
            {
                var low = lightSource.DutyCycleToPower.Interpolate(0);
                var high = lightSource.DutyCycleToPower.Interpolate(1);
                var clamped = Math.Max(Math.Min(low, high), Math.Min(Math.Max(low, high), power.Value));
                dutyCycle = Math.Max(0, Math.Min(1, lightSource.PowerToDutyCycle.Interpolate(clamped)));
            }

            result[pair.Key] = new CalibratedLightSource(
                lightSource.LightSource, lightSource.DutyCycleToPower, lightSource.PowerToDutyCycle, dutyCycle);
        }
        return result;
    }

    static double? TaskPower(FipCameraSource channel, AindPhysioFipTaskLogic taskLogic)
    {
        if (taskLogic == null || taskLogic.TaskParameters == null || taskLogic.TaskParameters.Power == null) return null;
        var power = taskLogic.TaskParameters.Power;
        switch (channel)
        {
            case FipCameraSource.Green: return power.Blue;
            case FipCameraSource.Iso: return power.Uv;
            case FipCameraSource.Red: return power.Lime;
            default: return null;
        }
    }
}

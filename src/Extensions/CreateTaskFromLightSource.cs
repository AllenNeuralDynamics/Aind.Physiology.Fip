using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using AindPhysiologyFip.Rig;
using MathNet.Numerics.Interpolation;

[Combinator]
[Description("Takes a light source and returns a calibrated light source.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CreateTaskFromLightSource
{
    public IObservable<CalibratedLightSource> Process(IObservable<LightSource> source)
    {
        return source.Select(value => {
            // We take care of the calibration
            if (value== null) throw new ArgumentNullException("Input is null.");
            var calibrationData = value.Calibration.Output.PowerLut;

            IInterpolation interpolator = null;

            if (calibrationData != null){
                Dictionary<double, double> lut = calibrationData.ToDictionary(
                    entry => double.Parse(entry.Key),
                    entry => entry.Value
                );
                interpolator = MakeInterpolator(lut);
            }
            else{
                var unityLut = new Dictionary<double, double> { { 0, 0 }, { 1, 1 } };
                interpolator = MakeInterpolator(unityLut);
            }

            // Interpolate the power:
            var power = value.Power;
            var calibratedDutyCycle = interpolator.Interpolate(power);
            return new CalibratedLightSource(value, interpolator, calibratedDutyCycle);
            }
        );
    }


    private static IInterpolation MakeInterpolator(Dictionary<double, double> lut)
    {
        var sortedKeys = lut.Keys.OrderBy(k => k).ToArray();
        var sortedValues = sortedKeys.Select(k => lut[k]).ToArray();

        return MathNet.Numerics.Interpolate.Linear(sortedKeys, sortedValues);
    }
}

public class CalibratedLightSource{

    public CalibratedLightSource(LightSource lightSource, IInterpolation interpolator, double calibratedDutyCycle)
    {
        this.LightSource = lightSource;
        this.Interpolator = interpolator;
        this.CalibratedDutyCycle = calibratedDutyCycle;
    }
    public readonly LightSource LightSource;
    public readonly IInterpolation Interpolator;
    public readonly double CalibratedDutyCycle;
}

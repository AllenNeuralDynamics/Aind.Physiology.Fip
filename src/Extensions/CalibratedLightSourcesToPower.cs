using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Reactive.Linq;
using AindPhysiologyFip;
using FipExtensions;
using Newtonsoft.Json;

[Combinator]
[Description("Converts the calibrated light sources dictionary into a LightSourceOutput (power and calibration state per light source), suitable for serialization.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class CalibratedLightSourcesToPower
{
    public IObservable<LightSourceOutput> Process(IObservable<IDictionary<FipCameraSource, CalibratedLightSource>> source)
    {
        return source.Select(lightSources => new LightSourceOutput
        {
            // Camera channel -> light source: Green -> Blue, Iso -> Uv, Red -> Lime
            Blue = GetOutput(lightSources, FipCameraSource.Green),
            Uv = GetOutput(lightSources, FipCameraSource.Iso),
            Lime = GetOutput(lightSources, FipCameraSource.Red)
        });
    }

    private static LightSourceOutputEntry GetOutput(IDictionary<FipCameraSource, CalibratedLightSource> lightSources, FipCameraSource key)
    {
        if (lightSources == null) throw new ArgumentNullException("lightSources");
        CalibratedLightSource calibrated;
        if (!lightSources.TryGetValue(key, out calibrated))
            throw new KeyNotFoundException("No calibrated light source found for camera source " + key + ".");
        var calibration = calibrated.LightSource == null ? null : calibrated.LightSource.Calibration;
        var isCalibrated = calibration != null && calibration.PowerLut != null;
        return new LightSourceOutputEntry
        {
            // Uncalibrated sources use an identity LUT, so the power is the duty cycle fraction (0-1).
            Power = calibrated.CalibratedPower,
            IsCalibrated = isCalibrated
        };
    }
}

public class LightSourceOutput
{
    [JsonProperty("blue")] public LightSourceOutputEntry Blue { get; set; }
    [JsonProperty("uv")] public LightSourceOutputEntry Uv { get; set; }
    [JsonProperty("lime")] public LightSourceOutputEntry Lime { get; set; }
}

public class LightSourceOutputEntry
{
    [JsonProperty("power")]
    [Description("Power in uW if calibrated; otherwise the duty cycle fraction (0-1), since uncalibrated sources use an identity LUT.")]
    public double Power { get; set; }

    [JsonProperty("is_calibrated")]
    [Description("True if the light source has a power LUT; false if it is driven by raw duty cycle.")]
    public bool IsCalibrated { get; set; }
}

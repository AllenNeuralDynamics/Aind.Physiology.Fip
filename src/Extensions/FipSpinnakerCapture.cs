using System.ComponentModel;
using Bonsai.Spinnaker;
using SpinnakerNET;
using OpenCV.Net;
using System;

namespace FipExtensions
{
    [Description("Configures and initializes a Spinnaker camera for fiber photometry acquisition.")]
    public class FipSpinnakerCapture : SpinnakerCapture
    {
        private const int binningFactor = 4;

        public FipSpinnakerCapture()
        {
            Gain = 0;
            Offset = new Point(0, 0);
        }

        [Description("The gain of the sensor.")]
        public double Gain { get; set; }

        [Description("The offset of the region of interest.")]
        public Point Offset { get; set; }

        private const int height = 200;
        private const int width = 200;

        protected override void Configure(IManagedCamera camera)
        {
            try { camera.AcquisitionStop.Execute(); }
            catch { }

            camera.BinningSelector.Value = BinningSelectorEnums.All.ToString();
            camera.BinningHorizontalMode.Value = BinningHorizontalModeEnums.Sum.ToString();
            camera.BinningVerticalMode.Value = BinningVerticalModeEnums.Sum.ToString();
            camera.BinningHorizontal.Value = binningFactor;
            camera.BinningVertical.Value = binningFactor;
            camera.DecimationHorizontalMode.Value = DecimationHorizontalModeEnums.Discard.ToString();
            camera.DecimationVerticalMode.Value = DecimationVerticalModeEnums.Discard.ToString();
            camera.DecimationHorizontal.Value = 1;
            camera.DecimationVertical.Value = 1;

            camera.AcquisitionFrameRateEnable.Value = false;
            camera.IspEnable.Value = false;

            camera.TriggerDelay.Value = camera.TriggerDelay.Min;
            camera.TriggerSelector.Value = TriggerSelectorEnums.FrameStart.ToString();
            camera.TriggerSource.Value = TriggerSourceEnums.Line0.ToString();
            camera.TriggerActivation.Value = TriggerActivationEnums.RisingEdge.ToString();
            camera.LineInputFilterSelector.Value = LineInputFilterSelectorEnums.Deglitch.ToString();

            camera.ExposureAuto.Value = ExposureAutoEnums.Off.ToString();
            camera.ExposureMode.Value = ExposureModeEnums.TriggerWidth.ToString();
            camera.BlackLevelSelector.Value = BlackLevelSelectorEnums.All.ToString();
            camera.BlackLevel.Value = 0;
            camera.DeviceLinkThroughputLimit.Value = camera.DeviceLinkThroughputLimit.Max;
            camera.GainAuto.Value = GainAutoEnums.Off.ToString();
            camera.Gain.Value = Gain;
            camera.GammaEnable.Value = false;

            camera.PixelFormat.Value = PixelFormatEnums.Mono16.ToString();
            camera.AdcBitDepth.Value = AdcBitDepthEnums.Bit10.ToString();

            SetRegionOfInterest(camera, new Rect(Offset.X, Offset.Y, width, height));

            base.Configure(camera);
        }

        /// <summary>
        /// Rounds <paramref name="value"/> to the nearest multiple of <paramref name="step"/>.
        /// Spinnaker requires OffsetX/OffsetY to be multiples of the active binning factor (4),
        /// because the sensor applies 4× binning before evaluating ROI coordinates.
        /// </summary>
        private static long SnapToGrid(int value, int step)
        {
            return (long)Math.Round((double)value / step) * step;
        }

        private static void SetRegionOfInterest(IManagedCamera camera, Rect crop)
        {
            if ((crop.Height == 0) || (crop.Width == 0))
            {
                if (crop.X != 0 || crop.Y != 0 || crop.Height != 0 || crop.Width != 0)
                {
                    throw new InvalidOperationException("If Height or Width is 0, all size arguments must be 0.");
                }
                camera.OffsetX.Value = 0;
                camera.OffsetY.Value = 0;
                camera.Width.Value = camera.WidthMax.Value;
                camera.Height.Value = camera.HeightMax.Value;
            }
            else
            {
                camera.Width.Value = crop.Width;
                camera.Height.Value = crop.Height;
                // Spinnaker rejects OffsetX/Y that are not multiples of the binning factor (4).
                // Snap to nearest valid value so the API call never fails on a rounding artefact.
                camera.OffsetX.Value = SnapToGrid(crop.X, binningFactor);
                camera.OffsetY.Value = SnapToGrid(crop.Y, binningFactor);
            }
        }
    }
}


using System;
using System.Windows.Forms;
using System.Collections.Generic;
using Bonsai;
using Bonsai.Design;
using OxyPlot;
using OxyPlot.Series;
using OxyPlot.WindowsForms;
using System.Drawing;
using OxyPlot.Axes;
using System.Linq;
using FipExtensions;
using Bonsai.Harp;

[assembly: TypeVisualizer(typeof(FipVisualizer),
    Target = typeof(Timestamped<CircleActivityCollection>))]




public class FipVisualizer : BufferedVisualizer{
    internal FipTimeSeries timeSeries;
    internal LineSeries lineSeries { get; private set; }
    private bool resetAxes = true;
    private double capacity = 100;
    public double Capacity {
        get { return capacity; }
        set { capacity = value; }
    }
    public string Label { get; set; }

    private OxyColor lineSeriesColor = OxyColor.FromRgb(0, 0, 255);
    public OxyColor LineSeriesColor{
        get { return lineSeriesColor; }
        set { lineSeriesColor = value; }
    }

    public override void Load(IServiceProvider provider)
        {
            timeSeries = new FipTimeSeries()
            {
                Capacity = Capacity,
                Dock = DockStyle.Fill,
                Size = new Size(800, 600),
            };

            var lineSeriesName = string.IsNullOrEmpty(Label) ? "TimeSeries" : Label;
            lineSeries = timeSeries.AddNewLineSeries(lineSeriesName, color: LineSeriesColor);

            timeSeries.ResetLineSeries(lineSeries);
            timeSeries.ResetAxes();

            var visualizerService = (IDialogTypeVisualizerService)provider.GetService(typeof(IDialogTypeVisualizerService));
            if (visualizerService != null)
            {
                visualizerService.AddControl(timeSeries);
            }
        }


        public  override void Show(object value){
        }

        protected override void Show(DateTime time, object value)
        {
            Timestamped<CircleActivityCollection> activity = (Timestamped<CircleActivityCollection>)value;
            timeSeries.AddToLineSeries(
                lineSeries: lineSeries,
                time: activity.Seconds,
                value: activity.Value[0].Activity.Val0
            );
        }

        protected override void ShowBuffer(IList<System.Reactive.Timestamped<object>> values)
        {
            base.ShowBuffer(values);
            var castedValues = values.Select(x => (Timestamped<CircleActivityCollection>)x.Value).ToList();
            if (values.Count > 0)
            {
                if (resetAxes)
                {
                    var time = castedValues.LastOrDefault().Seconds;
                    timeSeries.SetAxes(min: time - Capacity, max: time);
                }
                timeSeries.UpdatePlot();
            }
        }
        internal void ShowDataBuffer(IList<System.Reactive.Timestamped<object>> values, bool resetAxes = true)
        {
            this.resetAxes = resetAxes;
            ShowBuffer(values);
        }

        /// <inheritdoc/>
        public override void Unload()
        {
            if (!timeSeries.IsDisposed)
            {
                timeSeries.Dispose();
            }
        }
}

public class FipTimeSeries: UserControl{

    PlotModel plotModel;
    PlotView plotView;

    Axis xAxis;
    Axis yAxis;

    StatusStrip statusStrip;

    public double Capacity { get; set; }

    public FipTimeSeries(){

                plotView = new PlotView
                {
                    Size = Size,
                    Dock = DockStyle.Fill,
                };

                plotModel = new PlotModel();

                xAxis = new LinearAxis {
                    Position = AxisPosition.Bottom,
                    Title = "Seconds",
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    FontSize = 9
                };

                yAxis = new LinearAxis {
                    Position = AxisPosition.Left,
                    Title = "PixelIntensity",
                };

                plotModel.Axes.Add(xAxis);
                plotModel.Axes.Add(yAxis);

                plotView.Model = plotModel;
                Controls.Add(plotView);

                statusStrip = new StatusStrip
                {
                    Visible = true,
                };

                Controls.Add(statusStrip);
                AutoScaleDimensions = new SizeF(6F, 13F);
            }


        public LineSeries AddNewLineSeries(string lineSeriesName, OxyColor color)
        {
            LineSeries lineSeries = new LineSeries {
                Title = lineSeriesName,
                Color = color
            };
            plotModel.Series.Add(lineSeries);
            return lineSeries;
        }

        public void AddToLineSeries(LineSeries lineSeries, double time, double value)
        {
            lineSeries.Points.Add(new DataPoint(time, value));
        }

        public void SetAxes(double min, double max)
        {
            xAxis.Minimum = min;
            xAxis.Maximum = max;
        }

        public void UpdatePlot()
        {
            plotModel.InvalidatePlot(true);
        }

        public void ResetLineSeries(LineSeries lineSeries)
        {
            lineSeries.Points.Clear();
        }

        public void ResetModelSeries()
        {
            plotModel.Series.Clear();
        }

        public void ResetAxes()
        {
            xAxis.Reset();
            yAxis.Reset();
        }

}
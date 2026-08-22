using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Forms;
using AindPhysiologyFip;

[Combinator]
[Description("Check that the number of ROIs detected in the green/iso and red channels match.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class CheckRoiCountMatches
{
    public IObservable<bool> Process(IObservable<RoiSettings> source)
    {
        return source.Select(settings =>
        {
            if (settings.CameraGreenIsoRoi.Count == settings.CameraRedRoi.Count)
            {
                return true;
            }
            MessageBox.Show(
                string.Format("ROI count mismatch: Green/Iso has {0} ROIs but Red has {1}. Please retry ROI detection.", settings.CameraGreenIsoRoi.Count, settings.CameraRedRoi.Count),
                "ROI Count Mismatch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        });
    }
}

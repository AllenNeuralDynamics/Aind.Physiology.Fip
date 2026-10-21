using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Forms;
using AindPhysiologyFip;

[Combinator]
[Description("Checks the ROI settings before starting: the green/iso and red channels must have the same number of ROIs, and within each channel every ROI must have a ROI number and a fiber number, with no duplicates.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class ValidateRoiSettings
{
    public IObservable<bool> Process(IObservable<RoiSettings> source)
    {
        return source.Select(settings =>
        {
            var problems = new List<string>();
            var greenIsoRois = settings.CameraGreenIsoRoi ?? new List<FipCircle>();
            var redRois = settings.CameraRedRoi ?? new List<FipCircle>();

            if (greenIsoRois.Count != redRois.Count)
            {
                problems.Add(string.Format("ROI count mismatch: Green/Iso has {0} ROIs but Red has {1}.", greenIsoRois.Count, redRois.Count));
            }

            CheckNumbers("Green/Iso", "ROI number", greenIsoRois.Select(roi => roi.RoiNumber), problems);
            CheckNumbers("Green/Iso", "fiber number", greenIsoRois.Select(roi => roi.FiberNumber), problems);
            CheckNumbers("Red", "ROI number", redRois.Select(roi => roi.RoiNumber), problems);
            CheckNumbers("Red", "fiber number", redRois.Select(roi => roi.FiberNumber), problems);

            if (problems.Count == 0)
            {
                return true;
            }
            MessageBox.Show(
                "The ROI settings are not valid:\n\n- " + string.Join("\n- ", problems) + "\n\nPlease fix the ROIs before starting.",
                "Invalid ROI Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        });
    }

    // Every ROI in a channel must have this number set, and no two ROIs may share it.
    static void CheckNumbers(string channel, string label, IEnumerable<int?> numbers, List<string> problems)
    {
        var values = numbers.ToList();
        var missing = values.Count(number => !number.HasValue);
        if (missing > 0)
        {
            problems.Add(string.Format("{0}: {1} ROI(s) have no {2}.", channel, missing, label));
        }

        var duplicates = values
            .Where(number => number.HasValue)
            .GroupBy(number => number.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(number => number)
            .ToList();
        if (duplicates.Count > 0)
        {
            problems.Add(string.Format("{0}: duplicate {1}(s): {2}.", channel, label, string.Join(", ", duplicates)));
        }
    }
}

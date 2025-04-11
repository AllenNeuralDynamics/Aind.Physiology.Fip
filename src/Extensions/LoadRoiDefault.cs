using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using AindPhysiologyFip.Rig;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

[Combinator]
[Description("Loads the ROI default settings from disk.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LoadRoiDefault
{

    private string path = "../.local/default.json";
    [Description("The path to the ROI default settings file.")]
    [FileNameFilter("JSON|*.json|All Files|*.*")]
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    public string Path {
        get { return path; }
        set { path = value; }
    }

    public IObservable<RoiSettings> Process(IObservable<RoiSettings> source)
    {
        return source.Select(value =>{
            Console.WriteLine("")
            // 1. We attempt to load the settings from the schema file
            if (value != null) return value;
            // 2. If 1. fails, we attempt to load the default settings via the path property
            try{
                var json = File.ReadAllText(path);
                var deserialized = JsonConvert.DeserializeObject<RoiSettings>(json);
                value = deserialized;
                return JsonConvert.DeserializeObject<RoiSettings>(json);
            }
            catch (Exception e){
                Console.WriteLine("Error loading ROI default settings: %s", e.Message);
            }
            if (value != null) return value;
            // 3. If 2 fails, we create a default RoiSettings object
            else return DefaultRoiSettings();
        } );
    }


    private static Circle makeCircle(double x, double y, double radius){
        return new Circle(){
            Center = new AindPhysiologyFip.Rig.Point2f(){
                X = x,
                Y = y
            },
            Radius = radius
        };
    }

    private static RoiSettings DefaultRoiSettings(){
        return new RoiSettings(){
            BackgroundCameraGreenIso = makeCircle(0, 0, 20),
            BackgroundCameraRed = makeCircle(0, 0, 20),
            RoiCameraGreenIso = (new double[]{50, 150}).SelectMany(x => (new double[]{50, 150}).Select(y => makeCircle(x, y, 20))).ToList(),
            RoiCameraRed = (new double[]{50, 150}).SelectMany(x => (new double[]{50, 150}).Select(y => makeCircle(x, y, 20))).ToList(),
        };
    }
}

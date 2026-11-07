using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using AindPhysiologyFip;
using Bonsai;
using Newtonsoft.Json;

/// <summary>
/// Represents an operator that emits the text of a task logic file, or the JSON of a default
/// <see cref="AindPhysioFipTaskLogic"/> when no file is specified.
/// </summary>
[Description("Reads all the text of the file at Path and emits it as a string. If Path is empty, emits the JSON of a default AindPhysioFipTaskLogic instead.")]
public class ReadAllTextOrTaskDefault : Source<string>
{
    [Editor("Bonsai.Design.OpenFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
    [Description("The path of the task logic file to read. If empty, a default task logic is emitted instead.")]
    public string Path { get; set; }

    const int DefaultFiberCount = 4;

    // Four enabled fibers mapped 1:1 to ROIs, with every channel's power at 0.
    static AindPhysioFipTaskLogic CreateDefaultTaskLogic()
    {
        var parameters = new AindPhysioFipTaskParameters { Power = new LightSourcesPower { Blue = 0, Uv = 0, Lime = 0 } };
        for (int i = 0; i < DefaultFiberCount; i++)
        {
            parameters.Measurements.Add(new Measurement { FiberNumber = i, RoiNumber = i, Enabled = true });
        }
        return new AindPhysioFipTaskLogic { TaskParameters = parameters };
    }

    public override IObservable<string> Generate()
    {
        return Observable.Defer(() =>
        {
            if (string.IsNullOrEmpty(Path))
            {
                return Observable.Return(JsonConvert.SerializeObject(CreateDefaultTaskLogic()));
            }
            return Observable.Return(File.ReadAllText(Path));
        });
    }
}

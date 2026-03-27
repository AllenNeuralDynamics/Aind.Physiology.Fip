using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Windows.Forms;

[Combinator]
[Description("On an event, launches a confirmation dialog and emits the result as a boolean value.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
public class MessageBoxConfirmDialog
{
    private string text = "Are you sure?";
    public string Text
    {
        get { return text; }
        set { text = value; }
    }

    public IObservable<bool> Process(IObservable<string> source)
    {
        return source.Select(x => Observable.Create<bool>(observer =>
        {
            var result = MessageBox.Show(text, "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            observer.OnNext(result == DialogResult.Yes);
            observer.OnCompleted();
            return () => { };
        })).Switch();
    }
}

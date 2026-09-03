using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Xml.Serialization;
using Bonsai;
using Bonsai.Expressions;

/// <summary>
/// Represents an operator that filters an <see cref="ITriggerLaserTask"/> sequence down to only
/// the elements of the selected concrete type, casting each surviving element to that type.
/// </summary>
[DefaultProperty("Type")]
[Description("Filters an ITriggerLaserTask sequence down to only the elements of the selected concrete type (e.g. ContinuousLaserTask), casting each surviving element to that type. Equivalent to Rx's OfType<T>().")]
[WorkflowElementCategory(ElementCategory.Combinator)]
[XmlInclude(typeof(TypeMapping<ContinuousLaserTask>))]
[XmlInclude(typeof(TypeMapping<FipModeLaserTask>))]
[XmlInclude(typeof(TypeMapping<OffLaserTask>))]
public class LaserTaskOfType : SingleArgumentExpressionBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LaserTaskOfType"/> class.
    /// </summary>
    public LaserTaskOfType()
    {
        Type = new TypeMapping<ContinuousLaserTask>();
    }

    /// <summary>
    /// Gets or sets a value specifying the target type that elements of the sequence should be
    /// filtered down to and cast as.
    /// </summary>
    public TypeMapping Type { get; set; }

    /// <inheritdoc/>
    public override Expression Build(IEnumerable<Expression> arguments)
    {
        var typeMapping = (TypeMapping)Type;
        var returnType = typeMapping.GetType().GetGenericArguments()[0];
        return Expression.Call(
            typeof(LaserTaskOfType),
            "Process",
            new System.Type[] { returnType },
            Enumerable.Single(arguments));
    }

    private static IObservable<TResult> Process<TResult>(IObservable<ITriggerLaserTask> source) where TResult : ITriggerLaserTask
    {
        return source.Where(task => task is TResult).Select(task => (TResult)task);
    }
}

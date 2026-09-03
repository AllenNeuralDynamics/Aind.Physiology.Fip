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
/// Represents an operator that creates an <see cref="ITriggerLaserTask"/> of a type selected
/// from the property grid (<see cref="ContinuousLaserTask"/>, <see cref="FipModeLaserTask"/>, or
/// <see cref="OffLaserTask"/>), exposing that type's own properties (e.g. Channel/Power for
/// ContinuousLaserTask) directly on this node rather than nested under a sub-object. With no
/// source wired, emits the configured task once on subscribe; with a source wired, re-emits it
/// once for every notification from that source (e.g. wire a button's output to re-trigger it).
/// </summary>
[DefaultProperty("Payload")]
[Description("Creates an ITriggerLaserTask of the selected type (ContinuousLaserTask, FipModeLaserTask, or OffLaserTask). With no source wired, emits it once on subscribe; with a source wired, re-emits it once per source notification.")]
[WorkflowElementCategory(ElementCategory.Source)]
[XmlInclude(typeof(ContinuousLaserTask))]
[XmlInclude(typeof(FipModeLaserTask))]
[XmlInclude(typeof(OffLaserTask))]
public class CreateLaserTask : ExpressionBuilder, ICustomTypeDescriptor
{
    static readonly Range<int> argumentRange = Range.Create(0, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CreateLaserTask"/> class, defaulting to
    /// <see cref="OffLaserTask"/>.
    /// </summary>
    public CreateLaserTask()
    {
        Operator = new OffLaserTask();
    }

    /// <inheritdoc/>
    public override Range<int> ArgumentRange
    {
        get { return argumentRange; }
    }

    object Operator { get; set; }

    /// <summary>
    /// Gets or sets the concrete <see cref="ITriggerLaserTask"/> instance to build, selected by
    /// type from the property grid. Not meant to be wired as a workflow property directly; its
    /// own properties (once a type is selected) are what should be externalized/mapped instead.
    /// </summary>
    [DesignOnly(true)]
    [Externalizable(false)]
    [RefreshProperties(RefreshProperties.All)]
    [Category("Design")]
    [Description("Specifies the type of laser task to create.")]
    [TypeConverter(typeof(LaserTaskTypeConverter))]
    public object Payload
    {
        get { return Operator; }
        set { Operator = value; }
    }

    /// <inheritdoc/>
    public override Expression Build(IEnumerable<Expression> arguments)
    {
        var source = arguments.FirstOrDefault();
        var payload = Expression.Constant(Payload, typeof(ITriggerLaserTask));
        var combinator = Expression.Constant(this, typeof(CreateLaserTask));

        if (source == null)
        {
            return Expression.Call(combinator, "Process", null, payload);
        }
        else
        {
            var sourceType = source.Type.GetGenericArguments()[0];
            return Expression.Call(combinator, "Process", new Type[] { sourceType }, source, payload);
        }
    }

    IObservable<ITriggerLaserTask> Process(ITriggerLaserTask payload)
    {
        return Observable.Defer(delegate { return Observable.Return(payload); });
    }

    IObservable<ITriggerLaserTask> Process<TSource>(IObservable<TSource> source, ITriggerLaserTask payload)
    {
        return source.Select(delegate(TSource _) { return payload; });
    }

    #region ICustomTypeDescriptor Members

    static readonly Attribute[] EmptyAttributes = new Attribute[0];

    AttributeCollection ICustomTypeDescriptor.GetAttributes()
    {
        var attributes = TypeDescriptor.GetAttributes(GetType());
        var defaultProperty = TypeDescriptor.GetDefaultProperty(GetType());
        if (defaultProperty != null)
        {
            var instance = defaultProperty.GetValue(this);
            var instanceAttributes = TypeDescriptor.GetAttributes(instance);
            var description = instanceAttributes[typeof(DescriptionAttribute)] as DescriptionAttribute;
            if (description != null)
            {
                return AttributeCollection.FromExisting(attributes, description);
            }
        }

        return attributes;
    }

    string ICustomTypeDescriptor.GetClassName()
    {
        return TypeDescriptor.GetClassName(GetType());
    }

    string ICustomTypeDescriptor.GetComponentName()
    {
        return null;
    }

    TypeConverter ICustomTypeDescriptor.GetConverter()
    {
        return TypeDescriptor.GetConverter(GetType());
    }

    EventDescriptor ICustomTypeDescriptor.GetDefaultEvent()
    {
        return null;
    }

    PropertyDescriptor ICustomTypeDescriptor.GetDefaultProperty()
    {
        var defaultProperty = TypeDescriptor.GetDefaultProperty(GetType());
        return defaultProperty != null ? new FactoryTypePropertyDescriptor(defaultProperty) : null;
    }

    object ICustomTypeDescriptor.GetEditor(Type editorBaseType)
    {
        return TypeDescriptor.GetEditor(GetType(), editorBaseType);
    }

    EventDescriptorCollection ICustomTypeDescriptor.GetEvents()
    {
        return EventDescriptorCollection.Empty;
    }

    EventDescriptorCollection ICustomTypeDescriptor.GetEvents(Attribute[] attributes)
    {
        return EventDescriptorCollection.Empty;
    }

    PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties()
    {
        return ((ICustomTypeDescriptor)this).GetProperties(EmptyAttributes);
    }

    PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties(Attribute[] attributes)
    {
        var baseProperties = TypeDescriptor.GetProperties(GetType(), attributes);
        var defaultProperty = TypeDescriptor.GetDefaultProperty(GetType());
        if (defaultProperty != null)
        {
            var instance = defaultProperty.GetValue(this);
            var instanceProperties = TypeDescriptor.GetProperties(instance, attributes);
            var properties = new PropertyDescriptor[baseProperties.Count + instanceProperties.Count];
            for (int i = 0; i < baseProperties.Count; i++)
            {
                var baseProperty = baseProperties[i];
                if (baseProperty == defaultProperty)
                {
                    baseProperty = new FactoryTypePropertyDescriptor(defaultProperty);
                }

                properties[i] = baseProperty;
            }

            for (int i = 0; i < instanceProperties.Count; i++)
            {
                properties[i + baseProperties.Count] = instanceProperties[i];
            }
            return new PropertyDescriptorCollection(properties);
        }

        return baseProperties;
    }

    object ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor pd)
    {
        return pd != null && pd.ComponentType.IsAssignableFrom(GetType()) ? this : Operator;
    }

    class FactoryTypePropertyDescriptor : PropertyDescriptor
    {
        readonly PropertyDescriptor descriptor;

        public FactoryTypePropertyDescriptor(PropertyDescriptor descr)
            : base(descr)
        {
            descriptor = descr;
        }

        public override Type ComponentType
        {
            get { return descriptor.ComponentType; }
        }

        public override bool IsReadOnly
        {
            get { return false; }
        }

        public override Type PropertyType
        {
            get { return typeof(Type); }
        }

        public override bool CanResetValue(object component)
        {
            return true;
        }

        public override object GetValue(object component)
        {
            var value = descriptor.GetValue(component);
            return value != null ? value.GetType() : null;
        }

        public override void ResetValue(object component)
        {
            descriptor.SetValue(component, null);
        }

        public override void SetValue(object component, object value)
        {
            var currentValue = descriptor.GetValue(component);
            var newValue = Activator.CreateInstance((Type)value);

            var newProperties = TypeDescriptor.GetProperties(newValue);
            var currentProperties = TypeDescriptor.GetProperties(currentValue);
            foreach (PropertyDescriptor property in newProperties)
            {
                var mergeProperty = currentProperties[property.Name];
                if (mergeProperty != null && mergeProperty.PropertyType == property.PropertyType)
                {
                    var propertyValue = mergeProperty.GetValue(currentValue);
                    property.SetValue(newValue, propertyValue);
                }
            }

            descriptor.SetValue(component, newValue);
        }

        public override bool ShouldSerializeValue(object component)
        {
            return true;
        }
    }

    #endregion
}

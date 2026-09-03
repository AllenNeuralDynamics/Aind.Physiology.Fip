using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;

/// <summary>
/// Provides a type converter that lets <see cref="CreateLaserTask"/>'s property grid pick which
/// concrete <see cref="ITriggerLaserTask"/> to build, listing whichever types are declared via
/// <c>[XmlInclude]</c> on the property's owning type. Mirrors bonsai-rx/harp's internal
/// CombinatorTypeConverter (used by CreateMessageBuilder).
/// </summary>
class LaserTaskTypeConverter : TypeConverter
{
    internal static IEnumerable<Type> GetInstanceTypes(ITypeDescriptorContext context)
    {
        var builderType = context.Instance != null ? context.Instance.GetType() : context.PropertyDescriptor.ComponentType;
        var includeAttributes = (XmlIncludeAttribute[])builderType.GetCustomAttributes(typeof(XmlIncludeAttribute), true);
        if (includeAttributes.Length > 0)
        {
            return includeAttributes.Select(attribute => attribute.Type);
        }

        return Enumerable.Empty<Type>();
    }

    static string GetDisplayName(Type type)
    {
        var displayNameAttribute = (DisplayNameAttribute)Attribute.GetCustomAttribute(type, typeof(DisplayNameAttribute));
        if (displayNameAttribute != null && !string.IsNullOrEmpty(displayNameAttribute.DisplayName))
        {
            return displayNameAttribute.DisplayName;
        }

        return type.Name;
    }

    /// <inheritdoc/>
    public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType)
    {
        return sourceType == typeof(string);
    }

    /// <inheritdoc/>
    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        var typeName = value as string;
        if (typeName != null)
        {
            return GetInstanceTypes(context).FirstOrDefault(
                type => string.Equals(GetDisplayName(type), typeName, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <inheritdoc/>
    public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
    {
        var valueType = value as Type;
        if (destinationType == typeof(string) && valueType != null)
        {
            return GetDisplayName(valueType);
        }

        return null;
    }

    /// <inheritdoc/>
    public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
    {
        return true;
    }

    /// <inheritdoc/>
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
    {
        return true;
    }

    /// <inheritdoc/>
    public override TypeConverter.StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
    {
        var includeTypes = GetInstanceTypes(context).ToArray();
        return new TypeConverter.StandardValuesCollection(includeTypes);
    }
}

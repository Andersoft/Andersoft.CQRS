using System;

namespace Andersoft.CQRS.Abstractions;

/// <summary>
/// Marks a property on an event as the correlation identifier used to locate
/// the saga instance. Placed on saga event-handler methods.
/// </summary>
/// <remarks>
/// This attribute is optional metadata. The saga handler is responsible for
/// extracting the correlation ID at runtime. The attribute enables tooling
/// (source generators, analyzers) to verify correlation consistency.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class CorrelateByAttribute : Attribute
{
    /// <summary>
    /// The name of the property on the event that contains the correlation ID.
    /// </summary>
    public string EventProperty { get; }

    public CorrelateByAttribute(string eventProperty)
    {
        EventProperty = eventProperty;
    }
}

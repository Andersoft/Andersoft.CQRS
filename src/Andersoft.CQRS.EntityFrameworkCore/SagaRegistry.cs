using System;
using System.Collections.Generic;

namespace Andersoft.CQRS.EntityFrameworkCore;

/// <summary>Collects event and state types during saga registration.</summary>
public static class SagaRegistry
{
    private static readonly HashSet<Type> _eventTypes = new();
    private static readonly HashSet<Type> _stateTypes = new();

    /// <summary>Event types discovered during saga registration.</summary>
    public static IReadOnlyCollection<Type> EventTypes => _eventTypes;

    /// <summary>Saga state types discovered during saga registration.</summary>
    public static IReadOnlyCollection<Type> StateTypes => _stateTypes;

    internal static void Register(Abstractions.Saga saga)
    {
        foreach (var eventType in saga.Handlers.Keys)
            _eventTypes.Add(eventType);
    }

    /// <summary>Register a saga state type for EF Core auto-configuration.</summary>
    public static void RegisterState(Type stateType)
    {
        _stateTypes.Add(stateType);
    }

    /// <summary>For testing — clears the registry.</summary>
    internal static void Reset() { _eventTypes.Clear(); _stateTypes.Clear(); }
}

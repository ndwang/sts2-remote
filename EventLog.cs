using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Sts2Agent;

public class GameEvent
{
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
    public Dictionary<string, object>? Details { get; init; }
}

public static class EventLog
{
    private static readonly ConcurrentQueue<GameEvent> Events = new();

    public static void Add(string type, string message, Dictionary<string, object>? details = null)
    {
        Events.Enqueue(new GameEvent { Type = type, Message = message, Details = details });
        Plugin.LogDebug($"[Event] {type}: {message}");
    }

    public static List<GameEvent> DrainAll()
    {
        var result = new List<GameEvent>();
        while (Events.TryDequeue(out var evt))
            result.Add(evt);
        return result;
    }

    public static void Clear()
    {
        while (Events.TryDequeue(out _)) { }
    }
}

using System.Text.Json;

namespace Sts2Agent.Utilities;

public static class ActionResult
{
    public static string Ok(string message)
    {
        return JsonSerializer.Serialize(new { status = "ok", message });
    }

    public static string Error(string message)
    {
        Plugin.LogError($"Action error: {message}");
        return JsonSerializer.Serialize(new { error = message });
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sts2Agent.Contexts;
using Sts2Agent.Utilities;

namespace Sts2Agent;

public static class ActionExecutor
{
    private static readonly List<IContextHandler> Handlers = new()
    {
        new MapHandler(),
        new HandSelectionHandler(),
        new CardSelectionHandler(),
        new RewardsHandler(),
        new CombatHandler(),
        new EventContextHandler(),
        new RestSiteHandler(),
        new ShopHandler(),
        new TreasureHandler(),
        new GameOverHandler(),
        new CharacterSelectHandler(),
        new MainMenuHandler()
    };

    public static async Task<string> Execute(string actionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(actionJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return ActionResult.Error("Missing 'type' field");

            var type = typeProp.GetString()!;
            Plugin.LogDebug($"Executing action: {type}");

            var ctx = GameContext.Resolve();
            if (ctx == null)
                return ActionResult.Error("No active run or interactive screen");

            // Dispatch to the handler matching the current context
            var handler = Handlers.FirstOrDefault(h => h.Type == ctx.Type);
            if (handler != null)
            {
                var task = handler.TryExecute(type, root, ctx);
                if (task != null)
                {
                    var result = await task;
                    if (result != null)
                        return result;
                }
            }

            return ActionResult.Error($"Unknown action type '{type}' for context '{ctx.Type}'");
        }
        catch (JsonException)
        {
            return ActionResult.Error("Invalid JSON");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Action execution error: {e}");
            return ActionResult.Error(e.Message);
        }
    }

    /// <summary>
    /// Get the handler registry for use by GameStateSerializer.
    /// </summary>
    public static IReadOnlyList<IContextHandler> GetHandlers() => Handlers;
}

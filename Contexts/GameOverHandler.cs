using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class GameOverHandler : IContextHandler
{
    public ContextType Type => ContextType.GameOver;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var result = new Dictionary<string, object>();

        var history = RunManager.Instance?.History;
        result["victory"] = history?.Win ?? false;

        return result;
    }

    public List<Dictionary<string, object>> GetCommands(ContextInfo ctx)
    {
        return new List<Dictionary<string, object>>
        {
            new() { ["type"] = "return_to_menu" }
        };
    }

    public async Task<string>? TryExecute(string actionType, JsonElement root, ContextInfo ctx)
    {
        if (actionType == "return_to_menu")
            return await ReturnToMenu();
        return null;
    }

    private async Task<string> ReturnToMenu()
    {
        var screen = await GodotMainThread.RunAsync(() =>
        {
            var overlay = NOverlayStack.Instance?.Peek();
            return overlay as NGameOverScreen;
        });

        if (screen == null)
            return ActionResult.Error("Game over screen not found");

        var button = await GodotMainThread.RunAsync(() => screen.GetNode<NReturnToMainMenuButton>("%MainMenuButton"));
        if (button == null)
            return ActionResult.Error("Main menu button not found");

        var enabled = await GodotMainThread.RunAsync(() => button.IsEnabled);
        if (!enabled)
            return ActionResult.Error("Main menu button not yet enabled");

        await GodotMainThread.ClickAsync(button);
        Plugin.Log("Clicked return to main menu on game over screen");
        return ActionResult.Ok("Returning to main menu");
    }
}

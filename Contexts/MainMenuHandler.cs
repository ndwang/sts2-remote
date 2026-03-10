using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class MainMenuHandler : IContextHandler
{
    public ContextType Type => ContextType.MainMenu;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var result = new Dictionary<string, object>
        {
            ["screen"] = "main_menu",
            ["has_saved_run"] = SaveManager.Instance.HasRunSave
        };
        return result;
    }

    public List<Dictionary<string, object>> GetCommands(ContextInfo ctx)
    {
        var commands = new List<Dictionary<string, object>>();

        if (SaveManager.Instance.HasRunSave)
        {
            commands.Add(new Dictionary<string, object> { ["type"] = "continue_run" });
            commands.Add(new Dictionary<string, object> { ["type"] = "abandon_run" });
        }
        else
        {
            commands.Add(new Dictionary<string, object> { ["type"] = "start_run" });
        }

        return commands;
    }

    public async Task<string>? TryExecute(string actionType, JsonElement root, ContextInfo ctx)
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        var mainMenu = UiHelper.FindFirst<NMainMenu>(sceneRoot);
        if (mainMenu == null)
            return ActionResult.Error("Main menu not found");

        if (actionType == "continue_run")
        {
            var continueBtn = await GodotMainThread.RunAsync(() =>
                mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/ContinueButton"));
            if (continueBtn == null || !continueBtn.IsEnabled)
                return ActionResult.Error("Continue button not available");

            await GodotMainThread.ClickAsync(continueBtn);

            // Wait for the run to load
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(200);
                if (RunManager.Instance?.IsInProgress == true)
                    break;
            }

            Plugin.Log("Continued saved run");
            return ActionResult.Ok("Continued saved run");
        }

        if (actionType == "abandon_run")
        {
            var abandonBtn = await GodotMainThread.RunAsync(() =>
                mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/AbandonRunButton"));
            if (abandonBtn == null || !abandonBtn.IsEnabled)
                return ActionResult.Error("Abandon run button not available");

            await GodotMainThread.ClickAsync(abandonBtn);

            // The abandon button shows a confirmation popup — find and click "Yes"
            await Task.Delay(500);
            var yesBtn = await GodotMainThread.RunAsync(() =>
            {
                var popup = UiHelper.FindFirst<NAbandonRunConfirmPopup>(sceneRoot);
                return popup?.GetNode<NClickableControl>("VerticalPopup/YesButton");
            });

            if (yesBtn != null)
            {
                await GodotMainThread.ClickAsync(yesBtn);
                await Task.Delay(500);
            }

            Plugin.Log("Abandoned saved run");
            return ActionResult.Ok("Abandoned saved run");
        }

        if (actionType == "start_run")
        {
            // Click the singleplayer button
            var spButton = await GodotMainThread.RunAsync(() =>
                mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/SingleplayerButton"));
            if (spButton == null)
                return ActionResult.Error("Singleplayer button not found");
            if (!spButton.IsEnabled)
                return ActionResult.Error("Singleplayer button is not enabled");

            await GodotMainThread.ClickAsync(spButton);
            await Task.Delay(500);

            // Check if we landed on character select directly (first-time player)
            var charScreen = await GodotMainThread.RunAsync(() =>
                UiHelper.FindFirst<NCharacterSelectScreen>(sceneRoot));
            if (charScreen != null && charScreen.Visible)
            {
                Plugin.Log("Navigated directly to character select");
                return ActionResult.Ok("Navigated to character select");
            }

            // Click the Standard button on the singleplayer submenu
            var spSubmenu = await GodotMainThread.RunAsync(() =>
                UiHelper.FindFirst<NSingleplayerSubmenu>(sceneRoot));
            if (spSubmenu == null || !spSubmenu.Visible)
                return ActionResult.Error("Singleplayer submenu not found");

            var stdButton = await GodotMainThread.RunAsync(() =>
                spSubmenu.GetNode<NClickableControl>("StandardButton"));
            if (stdButton == null)
                return ActionResult.Error("Standard button not found");

            await GodotMainThread.ClickAsync(stdButton);

            // Wait for character select screen to appear
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(200);
                var cs = await GodotMainThread.RunAsync(() =>
                    UiHelper.FindFirst<NCharacterSelectScreen>(sceneRoot));
                if (cs != null && cs.Visible)
                {
                    Plugin.Log("Navigated to character select via singleplayer submenu");
                    return ActionResult.Ok("Navigated to character select");
                }
            }

            return ActionResult.Error("Timed out waiting for character select screen");
        }

        return null;
    }
}

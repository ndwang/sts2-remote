using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Localization;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class CharacterSelectHandler : IContextHandler
{
    private static readonly FieldInfo? SelectedButtonField =
        typeof(NCharacterSelectScreen).GetField("_selectedButton", BindingFlags.NonPublic | BindingFlags.Instance);

    public ContextType Type => ContextType.CharacterSelect;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        if (ctx.CharacterButtons == null) return null;

        var characters = new List<Dictionary<string, object>>();
        for (int i = 0; i < ctx.CharacterButtons.Count; i++)
        {
            var btn = ctx.CharacterButtons[i];
            if (!GodotObject.IsInstanceValid(btn)) continue;
            characters.Add(new Dictionary<string, object>
            {
                ["index"] = i,
                ["name"] = GetCharacterName(btn),
                ["locked"] = btn.IsLocked
            });
        }

        var result = new Dictionary<string, object>
        {
            ["characters"] = characters
        };

        // Show which character is currently selected
        if (ctx.CharacterSelectScreen != null && SelectedButtonField != null)
        {
            var selected = SelectedButtonField.GetValue(ctx.CharacterSelectScreen) as NCharacterSelectButton;
            if (selected != null && GodotObject.IsInstanceValid(selected))
                result["selected"] = GetCharacterName(selected);
        }

        return result;
    }

    public List<Dictionary<string, object>> GetCommands(ContextInfo ctx)
    {
        var commands = new List<Dictionary<string, object>>();
        if (ctx.CharacterButtons == null) return commands;

        for (int i = 0; i < ctx.CharacterButtons.Count; i++)
        {
            var btn = ctx.CharacterButtons[i];
            if (!GodotObject.IsInstanceValid(btn) || btn.IsLocked) continue;
            commands.Add(new Dictionary<string, object>
            {
                ["type"] = "select_character",
                ["index"] = i,
                ["name"] = GetCharacterName(btn)
            });
        }

        if (ctx.CharacterSelectScreen != null && GodotObject.IsInstanceValid(ctx.CharacterSelectScreen))
        {
            var embarkButton = ctx.CharacterSelectScreen.GetNode<Godot.Control>("ConfirmButton")
                as MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton;
            if (embarkButton != null && embarkButton.IsEnabled)
                commands.Add(new Dictionary<string, object> { ["type"] = "embark" });
        }

        return commands;
    }

    public async Task<string>? TryExecute(string actionType, JsonElement root, ContextInfo ctx)
    {
        if (actionType == "select_character")
        {
            var index = root.GetProperty("index").GetInt32();
            var buttons = ctx.CharacterButtons;
            if (buttons == null || index < 0 || index >= buttons.Count)
                return ActionResult.Error($"Character index {index} out of range (available: {buttons?.Count ?? 0})");

            var btn = buttons[index];
            if (!GodotObject.IsInstanceValid(btn))
                return ActionResult.Error("Character button is no longer valid");
            if (btn.IsLocked)
                return ActionResult.Error("Character is locked");

            await GodotMainThread.RunAsync(() => btn.Select());

            var name = GetCharacterName(btn);
            Plugin.Log($"Selected character: {name}");
            return ActionResult.Ok($"Selected character: {name}");
        }

        if (actionType == "embark")
        {
            var screen = ctx.CharacterSelectScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen))
                return ActionResult.Error("Character select screen not found");

            var embarkButton = await GodotMainThread.RunAsync(() =>
                screen.GetNode<Godot.Control>("ConfirmButton") as MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton);
            if (embarkButton == null)
                return ActionResult.Error("Embark button not found");
            if (!embarkButton.IsEnabled)
                return ActionResult.Error("Embark button is not enabled (select a character first)");

            await GodotMainThread.ClickAsync(embarkButton);

            // Wait for the run to start (RunManager becomes active)
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(200);
                if (RunManager.Instance?.IsInProgress == true)
                    break;
            }

            Plugin.Log("Embarked on run");
            return ActionResult.Ok("Embarked on run");
        }

        return null;
    }

    private static string GetCharacterName(NCharacterSelectButton btn)
    {
        var character = btn.Character;
        if (character == null) return "unknown";
        return TextHelper.StripBBCode(character.Title.GetFormattedText());
    }
}

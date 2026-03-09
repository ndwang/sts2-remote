using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Agent;

public static class ActionExecutor
{
    public static async Task<string> Execute(string actionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(actionJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return Error("Missing 'type' field");

            var type = typeProp.GetString();
            Plugin.Log($"Executing action: {type}");

            return type switch
            {
                "play_card" => await PlayCard(root),
                "choose_hand_cards" => await ChooseHandCards(root),
                "confirm_selection" => await ConfirmSelection(),
                "end_turn" => EndTurn(),
                "use_potion" => UsePotion(root),
                "select_map_node" => await SelectMapNode(root),
                "select_event_option" => await SelectEventOption(root),
                "select_reward" => await SelectReward(root),
                "select_card" => await SelectCard(root),
                "skip" => await Skip(),
                "proceed" => await Proceed(),
                "rest_option" => await SelectRestOption(root),
                "shop_open" => await ShopOpen(),
                "shop_buy" => await ShopBuy(root),
                "shop_leave" => await ShopLeave(),
                _ => Error($"Unknown action type: {type}")
            };
        }
        catch (JsonException)
        {
            return Error("Invalid JSON");
        }
        catch (Exception e)
        {
            Plugin.Log($"Action execution error: {e}");
            return Error(e.Message);
        }
    }

    // ── Combat Actions ──────────────────────────────────────────────

    private static async Task<string> PlayCard(JsonElement root)
    {
        if (CombatManager.Instance == null)
            return Error("Not in combat");

        if (CombatManager.Instance.IsOverOrEnding)
            return Error("Combat is ending");

        if (!CombatManager.Instance.IsPlayPhase)
            return Error("Not in play phase");

        var cardIndex = root.GetProperty("cardIndex").GetInt32();
        var player = RunManager.Instance.DebugOnlyGetState().Players[0];
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("No player combat state");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Card index {cardIndex} out of range (hand size: {hand.Count})");

        var card = hand[cardIndex];
        if (!card.CanPlay())
            return Error($"Card '{card.Title}' cannot be played");

        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;

        // Resolve target if needed
        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            if (root.TryGetProperty("targetIndex", out var targetProp))
            {
                var targetIndex = targetProp.GetInt32();
                var hittable = combatState.HittableEnemies;
                if (targetIndex < 0 || targetIndex >= hittable.Count)
                    return Error($"Target index {targetIndex} out of range (hittable: {hittable.Count})");
                target = hittable[targetIndex];
            }
            else
            {
                target = combatState.HittableEnemies.FirstOrDefault();
            }
            if (target == null)
                return Error("No valid target available");
        }
        else if (card.TargetType == TargetType.AnyAlly)
        {
            var allies = combatState.Allies.Where(c => c != null && c.IsAlive && c.IsPlayer && c != card.Owner.Creature);
            target = root.TryGetProperty("targetIndex", out var tp)
                ? allies.ElementAtOrDefault(tp.GetInt32())
                : allies.FirstOrDefault();
        }

        // Use TryManualPlay — the same path the game's UI uses (NCardPlay.TryPlayCard).
        // It validates targeting, enqueues a PlayCardAction in the action queue,
        // and the queue handles energy, animations, and effects properly.
        // Must run on the main thread because OnEnqueuePlayVfx touches Godot nodes.
        var tcs = new TaskCompletionSource<bool>();
        Callable.From(() =>
        {
            try
            {
                var success = card.TryManualPlay(target);
                tcs.SetResult(success);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }).CallDeferred();

        var played = await tcs.Task;
        if (!played)
            return Error($"Card '{card.Title}' play was rejected by the game");

        Plugin.Log($"Played card '{card.Title}'" + (target != null ? " targeting enemy" : ""));
        return Ok("Card played");
    }

    private static async Task<string> ChooseHandCards(JsonElement root)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection)
            return Error("Hand is not in card selection mode");

        var cardIndex = root.GetProperty("cardIndex").GetInt32();

        // Get the visible (selectable) holders in the same order as the serializer
        var visibleHolders = hand.CardHolderContainer.GetChildren()
            .OfType<NHandCardHolder>()
            .Where(h => h.Visible && h.CardNode?.Model != null)
            .ToList();

        if (cardIndex < 0 || cardIndex >= visibleHolders.Count)
            return Error($"Card index {cardIndex} out of range (available: {visibleHolders.Count})");

        var holder = visibleHolders[cardIndex];

        // Select the card by emitting Pressed signal on the holder (triggers OnHolderPressed)
        // Must run on main thread
        var tcs = new TaskCompletionSource<bool>();
        Callable.From(() =>
        {
            try
            {
                Plugin.Log($"ChooseHandCards: emitting Pressed on holder for '{holder.CardNode?.Model?.Title}'");
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                tcs.SetResult(true);
            }
            catch (Exception e)
            {
                Plugin.Log($"ChooseHandCards error: {e}");
                tcs.SetException(e);
            }
        }).CallDeferred();

        await tcs.Task;

        Plugin.Log($"Chose hand card {cardIndex}");
        return Ok("Hand card selected");
    }

    private static async Task<string> ConfirmSelection()
    {
        // Hand card selection confirm
        var hand = NPlayerHand.Instance;
        if (hand != null && hand.IsInCardSelection)
        {
            var confirmField = typeof(NPlayerHand).GetField("_selectModeConfirmButton",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (confirmField != null)
            {
                var confirmButton = confirmField.GetValue(hand) as NConfirmButton;
                if (confirmButton != null && confirmButton.IsEnabled)
                {
                    await ClickOnMainThread(confirmButton);
                    Plugin.Log("ConfirmSelection: clicked hand select confirm button");
                    return Ok("Selection confirmed");
                }
                return Error("Confirm button is not enabled (need to select more cards?)");
            }
        }

        return Error("No active selection to confirm");
    }

    private static string EndTurn()
    {
        var combatManager = CombatManager.Instance;
        if (combatManager == null)
            return Error("Not in combat");

        if (!combatManager.IsPlayPhase || !combatManager.IsInProgress)
            return Error("Not in play phase");

        var player = RunManager.Instance.DebugOnlyGetState().Players[0];

        if (combatManager.IsPlayerReadyToEndTurn(player))
            return Error("Turn already ended");

        // Enqueue EndPlayerTurnAction through the action queue, matching how
        // NEndTurnButton.CallReleaseLogic() does it. This ensures proper ordering
        // with other queued actions.
        var roundNumber = player.Creature.CombatState.RoundNumber;
        Callable.From(() =>
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(player, roundNumber));
        }).CallDeferred();

        Plugin.Log("Ended turn");
        return Ok("Turn ended");
    }

    private static string UsePotion(JsonElement root)
    {
        var slot = root.GetProperty("slot").GetInt32();
        var player = RunManager.Instance?.DebugOnlyGetState()?.Players[0];
        if (player == null)
            return Error("No active player");

        var potions = player.PotionSlots;
        if (slot < 0 || slot >= potions.Count)
            return Error($"Potion slot {slot} out of range");

        var potion = potions[slot];
        if (potion == null)
            return Error($"No potion in slot {slot}");

        // Resolve target
        Creature? target = null;
        if (root.TryGetProperty("targetIndex", out var targetProp))
        {
            var combatState = CombatManager.Instance?.DebugOnlyGetState();
            if (combatState != null)
            {
                var targetIndex = targetProp.GetInt32();
                var aliveEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();
                if (targetIndex < 0 || targetIndex >= aliveEnemies.Count)
                    return Error($"Target index {targetIndex} out of range");
                target = aliveEnemies[targetIndex];
            }
        }

        potion.EnqueueManualUse(target);
        Plugin.Log($"Used potion in slot {slot}");
        return Ok("Potion used");
    }

    // ── Map Actions ─────────────────────────────────────────────────

    private static async Task<string> SelectMapNode(JsonElement root)
    {
        var index = root.GetProperty("index").GetInt32();

        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null)
            return Error("No active run");

        var map = runState.Map;
        var visited = runState.VisitedMapCoords;
        var points = visited.Count == 0
            ? map.GetPointsInRow(0).ToList()
            : map.GetPoint(visited[visited.Count - 1])?.Children.ToList()
              ?? new List<MapPoint>();

        if (index < 0 || index >= points.Count)
            return Error($"Map node index {index} out of range (available: {points.Count})");

        var target = points[index];
        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null)
            return Error("Cannot access scene tree");

        var mapPointNodes = UiHelper.FindAll<NMapPoint>(sceneRoot);
        var targetNode = mapPointNodes.FirstOrDefault(mp =>
            mp.Point.coord.row == target.coord.row && mp.Point.coord.col == target.coord.col);

        if (targetNode == null)
            return Error($"Map node UI element not found for ({target.coord.row}, {target.coord.col})");

        await ClickOnMainThread(targetNode);

        // Wait for map screen to close (travel animation + room transition)
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            if (NMapScreen.Instance is not { IsOpen: true })
                break;
        }

        Plugin.Log($"Selected map node {index} ({target.coord.row}, {target.coord.col})");
        return Ok("Map node selected");
    }

    // ── Event Actions ───────────────────────────────────────────────

    private static async Task<string> SelectEventOption(JsonElement root)
    {
        var optionIndex = root.GetProperty("optionIndex").GetInt32();

        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null)
            return Error("Cannot access scene tree");

        var buttons = UiHelper.FindAll<NEventOptionButton>(sceneRoot)
            .Where(b => !b.Option.IsLocked)
            .ToList();

        if (optionIndex < 0 || optionIndex >= buttons.Count)
            return Error($"Event option index {optionIndex} out of range (available: {buttons.Count})");

        await ClickOnMainThread(buttons[optionIndex]);
        Plugin.Log($"Selected event option {optionIndex}");
        return Ok("Event option selected");
    }

    // ── Reward Actions ──────────────────────────────────────────────

    private static async Task<string> SelectReward(JsonElement root)
    {
        var rewardIndex = root.GetProperty("rewardIndex").GetInt32();

        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null)
            return Error("Cannot access scene tree");

        var buttons = UiHelper.FindAll<NRewardButton>(sceneRoot)
            .Where(b => b.IsEnabled)
            .ToList();

        if (rewardIndex < 0 || rewardIndex >= buttons.Count)
            return Error($"Reward index {rewardIndex} out of range (available: {buttons.Count})");

        await ClickOnMainThread(buttons[rewardIndex]);

        Plugin.Log($"Selected reward {rewardIndex}");
        return Ok("Reward selected");
    }

    private static async Task<string> SelectCard(JsonElement root)
    {
        var cardIndex = root.GetProperty("cardIndex").GetInt32();

        var overlayStack = NOverlayStack.Instance;
        var overlay = overlayStack?.Peek();

        if (overlay == null || overlay is not Node overlayNode)
            return Error($"No card selection screen open (overlay={overlay?.GetType().FullName ?? "null"})");

        // Verify _completionSource is set (CardsSelected() was called by the game).
        var tcsField = overlay.GetType().GetField("_completionSource",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (tcsField != null)
        {
            // Brief wait — the game disables cards for 350ms after opening (DisableCardsForShortTimeAfterOpening)
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (tcsField.GetValue(overlay) != null) break;
                await Task.Delay(100);
            }

            if (tcsField.GetValue(overlay) == null)
                return Error("Card selection screen not ready (_completionSource is null)");
        }

        var holders = UiHelper.FindAll<NCardHolder>(overlayNode);
        if (cardIndex < 0 || cardIndex >= holders.Count)
            return Error($"Card index {cardIndex} out of range (available: {holders.Count})");

        var holder = holders[cardIndex];

        // Determine screen type to decide signal and post-click behavior:
        // - NChooseACardSelectionScreen / NCardRewardSelectionScreen: click = immediate completion, no confirm
        // - NDeckCardSelectScreen: multi-select with preview+confirm when maxSelect reached
        // - NSimpleCardSelectScreen: may auto-complete or require manual confirm depending on prefs
        var isGridScreen = overlay is NCardGridSelectionScreen;
        var grid = isGridScreen ? UiHelper.FindFirst<NCardGrid>(overlayNode) : null;

        Plugin.Log($"SelectCard: overlay type={overlay.GetType().Name}, cardIndex={cardIndex}, holderCount={holders.Count}");

        // Emit the appropriate signal on the main thread
        var tcs = new TaskCompletionSource<bool>();
        Callable.From(() =>
        {
            try
            {
                if (grid != null)
                {
                    grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                }
                else
                {
                    holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                }
                tcs.TrySetResult(true);
            }
            catch (Exception e)
            {
                Plugin.Log($"SelectCard: signal emit error: {e}");
                tcs.TrySetException(e);
            }
        }).CallDeferred();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (completed != tcs.Task)
            return Error("SelectCard timed out emitting signal");
        await tcs.Task;

        // Case 1: Non-grid screens (NChooseACardSelectionScreen, NCardRewardSelectionScreen)
        // Click immediately completes selection — just wait for screen to close.
        if (!isGridScreen)
        {
            await WaitForOverlayClose(overlayNode, overlay);
            Plugin.Log($"Selected card {cardIndex}");
            return Ok("Card selected");
        }

        // Case 2+3: Grid screens — check if the screen auto-closed (NSimpleCardSelectScreen
        // with RequireManualConfirmation=false auto-completes when maxSelect reached).
        await Task.Delay(200);
        if (!GodotObject.IsInstanceValid(overlayNode) || NOverlayStack.Instance?.Peek() != overlay)
        {
            Plugin.Log($"Selected card {cardIndex} (screen auto-closed)");
            return Ok("Card selected");
        }

        // Screen still open. Check if a confirm button became enabled.
        // NDeckCardSelectScreen: selecting maxSelect cards triggers PreviewSelection() which
        // enables _previewConfirmButton. NSimpleCardSelectScreen with RequireManualConfirmation:
        // enables _confirmButton when selectedCards >= minSelect.
        var confirmButtons = UiHelper.FindAll<NConfirmButton>(overlayNode);
        NConfirmButton? enabledButton = confirmButtons.FirstOrDefault(b => b.IsEnabled);

        // If not immediately enabled, give a short window for PreviewSelection() deferred call
        if (enabledButton == null)
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(100);
                enabledButton = confirmButtons.FirstOrDefault(b => b.IsEnabled);
                if (enabledButton != null) break;
            }
        }

        if (enabledButton != null)
        {
            await ClickOnMainThread(enabledButton);
            Plugin.Log($"SelectCard: clicked confirm button");
            await WaitForOverlayClose(overlayNode, overlay);
        }
        else
        {
            // Partial multi-select — not enough cards selected yet, return immediately
            Plugin.Log($"SelectCard: partial selection (no confirm enabled yet)");
        }

        Plugin.Log($"Selected card {cardIndex}");
        return Ok("Card selected");
    }

    private static async Task WaitForOverlayClose(Node overlayNode, object overlay, int timeoutMs = 5000)
    {
        var iterations = timeoutMs / 100;
        for (int i = 0; i < iterations; i++)
        {
            await Task.Delay(100);
            if (!GodotObject.IsInstanceValid(overlayNode) || NOverlayStack.Instance?.Peek() != overlay)
                return;
        }
        Plugin.Log("WaitForOverlayClose: timed out");
    }

    private static async Task<string> Skip()
    {
        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null)
            return Error("Cannot access scene tree");

        // Try skip button on card selection screen
        var skipButton = UiHelper.FindFirst<NChoiceSelectionSkipButton>(sceneRoot);
        if (skipButton != null)
        {
            await ClickOnMainThread(skipButton);
            Plugin.Log("Clicked skip button");
            return Ok("Skipped");
        }

        // Fallback: try proceed button
        var proceedButton = UiHelper.FindFirst<NProceedButton>(sceneRoot);
        if (proceedButton != null)
        {
            await ClickOnMainThread(proceedButton);
            Plugin.Log("Clicked proceed (as skip fallback)");
            return Ok("Skipped (proceed)");
        }

        return Error("No skip or proceed button found");
    }

    private static async Task<string> Proceed()
    {
        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null)
            return Error("Cannot access scene tree");

        // Prefer the proceed button from the current overlay (rewards screen),
        // since rooms (combat, treasure, etc.) also have their own NProceedButton instances
        NProceedButton? button = null;
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is Node overlayNode)
            button = UiHelper.FindFirst<NProceedButton>(overlayNode);
        button ??= UiHelper.FindFirst<NProceedButton>(sceneRoot);

        if (button != null)
        {
            await ClickOnMainThread(button);
            Plugin.Log("Clicked proceed");
            return Ok("Proceeded");
        }

        // Finished events use an NEventOptionButton with IsProceed=true
        var eventProceed = UiHelper.FindAll<NEventOptionButton>(sceneRoot)
            .FirstOrDefault(b => b.Option.IsProceed);
        if (eventProceed != null)
        {
            await ClickOnMainThread(eventProceed);
            Plugin.Log("Clicked event proceed");
            return Ok("Proceeded");
        }

        return Error("No proceed button found");
    }

    // ── Rest Site Actions ───────────────────────────────────────────

    private static async Task<string> SelectRestOption(JsonElement root)
    {
        var option = root.GetProperty("option").GetString();

        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null)
            return Error("Cannot access scene tree");

        var buttons = UiHelper.FindAll<NRestSiteButton>(sceneRoot)
            .Where(b => b.Option.IsEnabled)
            .ToList();

        if (buttons.Count == 0)
            return Error("No rest site options available");

        // Match by option ID (e.g., "rest", "upgrade", "smith", "recall")
        var match = buttons.FirstOrDefault(b =>
            b.Option.OptionId.Equals(option, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            // Fallback: try by index if option is a number
            if (int.TryParse(option, out var idx) && idx >= 0 && idx < buttons.Count)
                match = buttons[idx];
            else
                return Error($"Rest option '{option}' not found. Available: {string.Join(", ", buttons.Select(b => b.Option.OptionId))}");
        }

        await ClickOnMainThread(match);
        Plugin.Log($"Selected rest option: {option}");
        return Ok("Rest option selected");
    }

    // ── Shop Actions ────────────────────────────────────────────────

    private static async Task<string> ShopOpen()
    {
        var nRoom = MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance;
        if (nRoom == null)
            return Error("Not in shop");

        if (nRoom.Inventory?.IsOpen == true)
            return Error("Shop already open");

        var merchantButton = nRoom.MerchantButton;
        if (merchantButton == null)
            return Error("Merchant button not found");

        await ClickOnMainThread(merchantButton);
        Plugin.Log("Opened shop inventory");
        return Ok("Shop opened");
    }

    private static async Task<string> ShopBuy(JsonElement root)
    {
        var itemIndex = root.GetProperty("itemIndex").GetInt32();

        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState?.CurrentRoom is not MerchantRoom shopRoom)
            return Error("Not in shop");

        var inv = shopRoom.Inventory;
        if (inv == null)
            return Error("No shop inventory");

        // Build flat list of stocked items in the same order as GameStateSerializer
        var entries = new List<MerchantEntry>();
        foreach (var e in inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries))
            if (e.IsStocked) entries.Add(e);
        foreach (var e in inv.RelicEntries)
            if (e.IsStocked) entries.Add(e);
        foreach (var e in inv.PotionEntries)
            if (e.IsStocked) entries.Add(e);
        try { if (inv.CardRemovalEntry?.IsStocked == true) entries.Add(inv.CardRemovalEntry); } catch { }

        if (itemIndex < 0 || itemIndex >= entries.Count)
            return Error($"Shop item index {itemIndex} out of range (available: {entries.Count})");

        var entry = entries[itemIndex];
        if (!entry.EnoughGold)
            return Error("Not enough gold");

        // Must run on main thread — card removal triggers NOverlayStack.Push
        // which creates Godot nodes (NDeckCardSelectScreen, etc.)
        var tcs = new TaskCompletionSource<bool>();
        Callable.From(() =>
        {
            try
            {
                entry.OnTryPurchaseWrapper(inv);
                tcs.SetResult(true);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }).CallDeferred();

        await tcs.Task;
        await Task.Delay(300);

        Plugin.Log($"Bought shop item {itemIndex}");
        return Ok("Item purchased");
    }

    private static async Task<string> ShopLeave()
    {
        // Shop leave is just proceed
        return await Proceed();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Clicks a button on the main Godot thread. This is critical because
    /// ForceClick triggers game logic that may call AddChildSafely, which
    /// defers AddChild when not on the main thread, breaking async flows.
    /// </summary>
    private static async Task ClickOnMainThread(NClickableControl button, int delayMs = 300)
    {
        var tcs = new TaskCompletionSource<bool>();
        Callable.From(() =>
        {
            try
            {
                button.ForceClick();
                tcs.SetResult(true);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }).CallDeferred();

        await tcs.Task;
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    private static Node? GetSceneRoot()
    {
        try
        {
            return ((SceneTree)Engine.GetMainLoop()).Root;
        }
        catch
        {
            return null;
        }
    }

    private static string Ok(string message)
    {
        return JsonSerializer.Serialize(new { status = "ok", message });
    }

    private static string Error(string message)
    {
        Plugin.Log($"Action error: {message}");
        return JsonSerializer.Serialize(new { error = message });
    }
}

using System;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Agent;

/// <summary>
/// Monitors game state and signals when the game is idle and waiting for player input.
/// Replaces direct SignalDecisionPoint calls with unified stability detection.
/// </summary>
public static class GameStabilityDetector
{
    public static event Action? OnBecameStable;
    private static bool _pendingCheck;
    private static bool _wasStable;
    private static bool _subscribedToActionExecutor;
    private static CombatManager? _subscribedCombat;

    public static void Initialize()
    {
        TrySubscribeToActionExecutor();
        Plugin.Log("GameStabilityDetector initialized.");
    }

    private static void TrySubscribeToActionExecutor()
    {
        if (_subscribedToActionExecutor) return;
        try
        {
            var executor = RunManager.Instance?.ActionExecutor;
            if (executor == null) return;
            executor.BeforeActionExecuted += _ => { _wasStable = false; };
            executor.AfterActionExecuted += _ => ScheduleStabilityCheck();
            _subscribedToActionExecutor = true;
            Plugin.Log("Subscribed to ActionExecutor events.");
        }
        catch (Exception e)
        {
            Plugin.Log($"ActionExecutor not ready yet: {e.Message}");
        }
    }

    public static void SubscribeToCombat(CombatManager cm)
    {
        TrySubscribeToActionExecutor();
        UnsubscribeFromCombat();
        _subscribedCombat = cm;
        cm.PlayerActionsDisabledChanged += OnCombatStateChanged;
        cm.TurnStarted += OnCombatStateChanged;
        cm.CombatEnded += OnCombatEnded;
    }

    /// <summary>
    /// Called by HttpServer before executing any action, so that the
    /// stability detector knows to re-signal when the game becomes stable again.
    /// </summary>
    public static void OnActionStarting()
    {
        _wasStable = false;
    }

    public static void OnHandSelectionEntered()
    {
        Plugin.Log("Hand selection entered — scheduling stability check");
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void OnRoomEntered()
    {
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void OnOverlayChanged()
    {
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void ScheduleStabilityCheck()
    {
        if (_pendingCheck) return;
        _pendingCheck = true;
        Callable.From(CheckStability).CallDeferred();
    }

    private static void CheckStability()
    {
        _pendingCheck = false;
        var stable = IsStable();
        Plugin.Log($"CheckStability: stable={stable}, _wasStable={_wasStable}");
        if (stable && !_wasStable)
        {
            _wasStable = true;
            Plugin.Log("=== GAME STABLE ===");
            Plugin.Log(GameStateSerializer.Serialize());
            OnBecameStable?.Invoke();
        }
        else if (!stable)
        {
            _wasStable = false;
        }
    }

    public static bool IsStable()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null)
        {
            Plugin.Log("IsStable: runState=null → false");
            return false;
        }

        // Map screen takes priority — if it's open, we're past combat
        var mapScreen = NMapScreen.Instance;
        if (mapScreen is { IsOpen: true })
        {
            Plugin.Log("IsStable: map screen open → true");
            return true;
        }

        var cm = CombatManager.Instance;
        if (cm != null && cm.IsInProgress && !cm.IsOverOrEnding)
        {
            // Hand card selection mode (e.g., discard/exhaust prompts) is a decision point
            // even though the action queue is still running
            var hand = NPlayerHand.Instance;
            if (hand != null && hand.IsInCardSelection)
            {
                Plugin.Log("IsStable: hand card selection active → true");
                return true;
            }

            var result = cm.IsPlayPhase
                && !cm.PlayerActionsDisabled
                && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null;
            Plugin.Log($"IsStable: combat path → IsPlayPhase={cm.IsPlayPhase}, ActionsDisabled={cm.PlayerActionsDisabled}, RunningAction={RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().Name ?? "null"} → {result}");
            return result;
        }

        // Outside combat: check for interactive context
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay != null)
        {
            Plugin.Log($"IsStable: overlay={overlay.GetType().Name} → true");
            return true;
        }

        var room = runState.CurrentRoom;
        var roomResult = room is RestSiteRoom or MerchantRoom or TreasureRoom;
        if (room is EventRoom eventRoom)
        {
            var evt = eventRoom.LocalMutableEvent;
            if (evt is AncientEventModel && TryAdvanceAncientDialogue())
            {
                // Dialogue was advanced; re-check stability after a delay
                return false;
            }
            var evtResult = evt != null && (evt.CurrentOptions.Count > 0 || evt.IsFinished);
            Plugin.Log($"IsStable: EventRoom, hasEvent={evt != null}, options={evt?.CurrentOptions.Count ?? 0}, finished={evt?.IsFinished} → {evtResult}");
            return evtResult;
        }

        Plugin.Log($"IsStable: room={room?.GetType().Name ?? "null"}, noOverlay → {roomResult}");
        return roomResult;
    }

    private static void OnCombatStateChanged(CombatState _) => ScheduleStabilityCheck();

    private static void OnCombatEnded(CombatRoom _)
    {
        UnsubscribeFromCombat();
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    /// <summary>
    /// If an ancient event is showing dialogue (hitbox visible), click it to advance
    /// and schedule a re-check. Returns true if dialogue was advanced.
    /// </summary>
    private static bool TryAdvanceAncientDialogue()
    {
        try
        {
            var sceneRoot = ((SceneTree)Engine.GetMainLoop()).Root;
            var ancientLayout = UiHelper.FindFirst<NAncientEventLayout>(sceneRoot);
            if (ancientLayout == null) return false;

            var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
            if (hitbox == null || !hitbox.Visible || !hitbox.IsEnabled) return false;

            Plugin.Log("Ancient dialogue detected, auto-advancing...");
            hitbox.EmitSignal(NClickableControl.SignalName.Released, hitbox);

            // Schedule another check after the animation
            var timer = ancientLayout.GetTree().CreateTimer(0.6);
            timer.Connect("timeout", Callable.From(ScheduleStabilityCheck));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log($"TryAdvanceAncientDialogue error: {e.Message}");
            return false;
        }
    }

    private static void UnsubscribeFromCombat()
    {
        if (_subscribedCombat == null) return;
        _subscribedCombat.PlayerActionsDisabledChanged -= OnCombatStateChanged;
        _subscribedCombat.TurnStarted -= OnCombatStateChanged;
        _subscribedCombat.CombatEnded -= OnCombatEnded;
        _subscribedCombat = null;
    }
}

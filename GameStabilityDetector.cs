using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Agent.Contexts;

namespace Sts2Agent;

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

    public static void OnScreenTransition()
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
        var ctx = GameContext.Resolve();
        if (ctx == null)
        {
            Plugin.Log("IsStable: no context → false");
            return false;
        }

        switch (ctx.Type)
        {
            case ContextType.Unknown:
                Plugin.Log("IsStable: unknown context → false");
                return false;

            case ContextType.Map:
                Plugin.Log("IsStable: map screen → true");
                return true;

            case ContextType.CardSelection:
            case ContextType.Rewards:
                Plugin.Log($"IsStable: overlay {ctx.Type} → true");
                return true;

            case ContextType.HandSelection:
                Plugin.Log("IsStable: hand card selection → true");
                return true;

            case ContextType.Combat:
            {
                var cm = CombatManager.Instance;
                var result = cm != null
                    && cm.IsPlayPhase
                    && !cm.PlayerActionsDisabled
                    && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null;
                Plugin.Log($"IsStable: combat → IsPlayPhase={cm?.IsPlayPhase}, ActionsDisabled={cm?.PlayerActionsDisabled}, RunningAction={RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().Name ?? "null"} → {result}");
                return result;
            }

            case ContextType.Event:
            {
                var evt = ctx.EventRoom!.LocalMutableEvent;
                if (evt is AncientEventModel && EventContextHandler.TryAdvanceAncientDialogue())
                    return false;
                var evtResult = evt != null && (evt.CurrentOptions.Count > 0 || evt.IsFinished);
                Plugin.Log($"IsStable: event → hasEvent={evt != null}, options={evt?.CurrentOptions.Count ?? 0}, finished={evt?.IsFinished} → {evtResult}");
                return evtResult;
            }

            case ContextType.RestSite:
            case ContextType.Shop:
                Plugin.Log($"IsStable: {ctx.Type} → true");
                return true;

            case ContextType.GameOver:
                Plugin.Log("IsStable: game over screen → true");
                return true;

            case ContextType.MainMenu:
                Plugin.Log("IsStable: main menu → true");
                return true;

            case ContextType.CharacterSelect:
                Plugin.Log("IsStable: character select → true");
                return true;

            case ContextType.Treasure:
            {
                if (TreasureRoomAutoPatch.AutoClickInProgress)
                {
                    Plugin.Log("IsStable: treasure auto-click in progress → false");
                    return false;
                }
                var treasureRoom = TreasureRoomAutoPatch.CurrentRoom;
                var proceedEnabled = treasureRoom != null
                    && GodotObject.IsInstanceValid(treasureRoom)
                    && treasureRoom.ProceedButton?.IsEnabled == true;
                Plugin.Log($"IsStable: treasure proceed={proceedEnabled} → {proceedEnabled}");
                return proceedEnabled;
            }

            default:
                return false;
        }
    }

    private static void OnCombatStateChanged(CombatState _) => ScheduleStabilityCheck();

    private static void OnCombatEnded(CombatRoom _)
    {
        UnsubscribeFromCombat();
        _wasStable = false;
        ScheduleStabilityCheck();
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

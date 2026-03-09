using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;

namespace Sts2Agent;

public static class GameStateSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null)
                return JsonSerializer.Serialize(new { error = "No active run" }, JsonOptions);

            var state = new Dictionary<string, object>();

            // Determine context: map screen takes priority since it's an overlay
            var room = runState.CurrentRoom;
            var mapOpen = NMapScreen.Instance is { IsOpen: true };
            var context = mapOpen ? "map" : GetContext(room);
            state["context"] = context;

            // Player info (always present if in a run)
            var players = runState.Players;
            if (players.Count > 0)
            {
                state["player"] = SerializePlayer(players[0]);
            }

            // Context-specific state
            switch (context)
            {
                case "combat":
                    var combatState = CombatManager.Instance?.DebugOnlyGetState();
                    if (combatState != null)
                        state["combat"] = SerializeCombat(combatState, players[0]);
                    break;

                case "event":
                    if (room is EventRoom eventRoom)
                        state["event"] = SerializeEvent(eventRoom);
                    break;

                case "map":
                    state["map"] = SerializeMap(runState);
                    break;

                case "rest":
                    if (room is RestSiteRoom restRoom)
                        state["rest"] = SerializeRestSite(restRoom);
                    state["map"] = SerializeMap(runState);
                    break;

                case "shop":
                    if (room is MerchantRoom shopRoom)
                        state["shop"] = SerializeShop(shopRoom, players[0]);
                    break;

                case "treasure":
                    state["treasure"] = SerializeTreasure();
                    break;
            }

            // Detect overlay screens (rewards, card selection) on top of any context
            // Skip when map is open — overlays are stale at that point
            if (context != "map")
            {
                var overlay = DetectOverlay();
                if (overlay != null)
                {
                    state["overlay"] = overlay;
                    // When an overlay is active, event options are hidden in the UI
                    if (state.ContainsKey("event") && state["event"] is Dictionary<string, object> eventDict)
                        eventDict["options"] = new List<object>();
                }
            }

            // Available commands based on current context
            state["available_commands"] = GetAvailableCommands();

            return JsonSerializer.Serialize(state, JsonOptions);
        }
        catch (Exception e)
        {
            return JsonSerializer.Serialize(new { error = e.Message }, JsonOptions);
        }
    }

    private static string GetContext(AbstractRoom? room)
    {
        return room switch
        {
            CombatRoom => "combat",
            EventRoom => "event",
            // Map is detected via NMapScreen.IsOpen, not room type
            MerchantRoom => "shop",
            RestSiteRoom => "rest",
            TreasureRoom => "treasure",
            _ => "unknown"
        };
    }

    private static Dictionary<string, object> SerializePlayer(Player player)
    {
        var result = new Dictionary<string, object>
        {
            ["hp"] = player.Creature.CurrentHp,
            ["maxHp"] = player.Creature.MaxHp,
            ["gold"] = player.Gold
        };

        // Relics
        result["relics"] = player.Relics.Select(r => new Dictionary<string, object>
        {
            ["name"] = SafeLocString(() => r.Title),
            ["description"] = GetRelicDescription(r)
        }).ToList();

        // Potions
        result["potions"] = player.PotionSlots
            .Select((p, i) => p == null ? null : new Dictionary<string, object>
            {
                ["slot"] = i,
                ["name"] = SafeLocString(() => p.Title),
                ["description"] = GetPotionDescription(p)
            })
            .Where(p => p != null)
            .ToList();

        // Full deck
        result["deck"] = player.Deck.Cards.Select(SerializeCardBrief).ToList();

        return result;
    }

    private static Dictionary<string, object> SerializeCombat(CombatState combatState, Player player)
    {
        var pcs = player.PlayerCombatState;
        var result = new Dictionary<string, object>
        {
            ["round"] = combatState.RoundNumber,
            ["currentSide"] = combatState.CurrentSide.ToString()
        };

        if (pcs != null)
        {
            result["energy"] = pcs.Energy;
            result["stars"] = pcs.Stars;

            // Hand
            result["hand"] = pcs.Hand.Cards
                .Select((c, i) => SerializeCardInHand(c, i))
                .ToList();

            // Pile counts
            result["drawPileCount"] = pcs.DrawPile.Cards.Count;
            result["discardPileCount"] = pcs.DiscardPile.Cards.Count;
            result["exhaustPileCount"] = pcs.ExhaustPile.Cards.Count;
        }

        // Player creature in combat
        var playerCreature = player.Creature;
        result["playerBlock"] = playerCreature.Block;
        result["playerPowers"] = SerializePowers(playerCreature.Powers);

        // Enemies
        result["enemies"] = combatState.Enemies
            .Where(e => e.IsAlive)
            .Select((e, i) => SerializeEnemy(e, i, combatState))
            .ToList();

        return result;
    }

    private static Dictionary<string, object> SerializeCardInHand(CardModel card, int index)
    {
        var result = new Dictionary<string, object>
        {
            ["index"] = index,
            ["name"] = card.Title,
            ["description"] = GetCardDescription(card),
            ["targetType"] = card.TargetType.ToString(),
            ["playable"] = card.CanPlay()
        };

        if (card.EnergyCost != null)
        {
            if (card.EnergyCost.CostsX)
                result["cost"] = "X";
            else
                result["cost"] = card.EnergyCost.GetWithModifiers(CostModifiers.All);
        }

        return result;
    }

    private static Dictionary<string, object> SerializeCardBrief(CardModel card)
    {
        var result = new Dictionary<string, object>
        {
            ["name"] = card.Title,
            ["description"] = GetCardDescription(card)
        };

        if (card.EnergyCost != null)
        {
            if (card.EnergyCost.CostsX)
                result["cost"] = "X";
            else
                result["cost"] = card.EnergyCost.Canonical;
        }

        return result;
    }

    private static Dictionary<string, object> SerializeEnemy(Creature enemy, int index, CombatState combatState)
    {
        var result = new Dictionary<string, object>
        {
            ["index"] = index,
            ["hp"] = enemy.CurrentHp,
            ["maxHp"] = enemy.MaxHp,
            ["block"] = enemy.Block,
            ["powers"] = SerializePowers(enemy.Powers)
        };

        var monster = enemy.Monster;
        if (monster != null)
        {
            result["name"] = SafeLocString(() => monster.Title);

            // Intent info
            var intents = monster.NextMove?.Intents;
            if (intents != null && intents.Count > 0)
            {
                result["intents"] = intents.Select(intent =>
                {
                    var intentDict = new Dictionary<string, object>
                    {
                        ["type"] = intent.IntentType.ToString()
                    };

                    // Extract damage info from attack intents
                    if (intent is AttackIntent attackIntent)
                    {
                        try
                        {
                            var allies = combatState.Creatures
                                .Where(c => c.Player != null);
                            intentDict["damage"] = attackIntent.GetTotalDamage(allies, enemy);
                            if (attackIntent.Repeats > 1)
                                intentDict["hits"] = attackIntent.Repeats;
                        }
                        catch
                        {
                            // Damage calc may fail in some edge cases
                        }
                    }

                    return intentDict;
                }).ToList();
            }
        }

        return result;
    }

    private static List<Dictionary<string, object>> SerializePowers(IReadOnlyList<PowerModel> powers)
    {
        return powers.Select(p => new Dictionary<string, object>
        {
            ["name"] = SafeLocString(() => p.Title),
            ["amount"] = p.Amount,
            ["description"] = GetPowerDescription(p)
        }).ToList();
    }

    private static Dictionary<string, object> SerializeEvent(EventRoom eventRoom)
    {
        // Use the local mutable event (safe for reading runtime state).
        // Fall back to canonical for title only if mutable isn't available yet.
        var evt = eventRoom.LocalMutableEvent;
        if (evt == null)
        {
            return new Dictionary<string, object>
            {
                ["title"] = SafeLocString(() => eventRoom.CanonicalEvent.Title),
                ["description"] = "",
                ["options"] = new List<object>()
            };
        }

        var result = new Dictionary<string, object>
        {
            ["title"] = SafeLocString(() => evt.Title)
        };

        var desc = evt.Description;
        try
        {
            if (desc != null)
                result["description"] = StripBBCode(desc.GetFormattedText());
            else
                result["description"] = SafeLocString(() => evt.InitialDescription);
        }
        catch
        {
            result["description"] = "";
        }

        result["options"] = evt.CurrentOptions.Select((opt, i) =>
        {
            var optDict = new Dictionary<string, object>
            {
                ["index"] = i,
                ["label"] = SafeLocString(() => opt.Title),
                ["locked"] = opt.IsLocked
            };
            try
            {
                var optDesc = opt.Description;
                if (optDesc != null)
                {
                    evt.DynamicVars.AddTo(optDesc);
                    optDict["description"] = StripBBCode(optDesc.GetFormattedText());
                }
                else
                {
                    optDict["description"] = "";
                }
            }
            catch
            {
                optDict["description"] = "";
            }
            return optDict;
        }).ToList();

        return result;
    }

    private static Dictionary<string, object> SerializeMap(RunState runState)
    {
        var result = new Dictionary<string, object>();

        var coord = runState.CurrentMapCoord;
        if (coord.HasValue)
        {
            result["currentCoord"] = new { row = coord.Value.row, col = coord.Value.col };
        }

        result["act"] = runState.CurrentActIndex + 1;

        // Available next nodes
        try
        {
            var map = runState.Map;
            var visited = runState.VisitedMapCoords;

            if (visited.Count == 0)
            {
                // First room: row 0 nodes are available
                var row0 = map.GetPointsInRow(0).ToList();
                result["availableNodes"] = row0.Select(p => new Dictionary<string, object>
                {
                    ["coord"] = new { row = p.coord.row, col = p.coord.col },
                    ["type"] = p.PointType.ToString()
                }).ToList();
            }
            else
            {
                var lastCoord = visited[visited.Count - 1];
                var currentPoint = map.GetPoint(lastCoord);
                if (currentPoint != null)
                {
                    result["availableNodes"] = currentPoint.Children.Select(p => new Dictionary<string, object>
                    {
                        ["coord"] = new { row = p.coord.row, col = p.coord.col },
                        ["type"] = p.PointType.ToString()
                    }).ToList();
                }
            }
        }
        catch
        {
            // Map traversal may fail in some states
        }

        return result;
    }

    private static Dictionary<string, object> SerializeRestSite(RestSiteRoom restRoom)
    {
        var result = new Dictionary<string, object>();

        result["options"] = restRoom.Options.Select((opt, i) => new Dictionary<string, object>
        {
            ["index"] = i,
            ["id"] = opt.OptionId,
            ["name"] = SafeLocString(() => opt.Title),
            ["description"] = SafeLocString(() => opt.Description),
            ["enabled"] = opt.IsEnabled
        }).ToList();

        return result;
    }

    private static Dictionary<string, object> SerializeShop(MerchantRoom shopRoom, Player player)
    {
        var result = new Dictionary<string, object>();
        var inv = shopRoom.Inventory;
        if (inv == null) return result;

        // Check if the shop inventory UI is open (shopkeeper has been clicked)
        var nRoom = MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance;
        bool isOpen = nRoom?.Inventory?.IsOpen == true;
        result["isOpen"] = isOpen;

        if (!isOpen)
        {
            result["items"] = new List<Dictionary<string, object>>();
            result["gold"] = player.Gold;
            return result;
        }

        var items = new List<Dictionary<string, object>>();
        var index = 0;

        // Cards
        foreach (var entry in inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries))
        {
            if (!entry.IsStocked) continue;
            var card = entry.CreationResult.Card;
            items.Add(new Dictionary<string, object>
            {
                ["index"] = index++,
                ["type"] = "card",
                ["name"] = card.Title.ToString(),
                ["description"] = GetCardDescription(card),
                ["cost"] = entry.Cost,
                ["affordable"] = entry.EnoughGold
            });
        }

        // Relics
        foreach (var entry in inv.RelicEntries)
        {
            if (!entry.IsStocked) continue;
            items.Add(new Dictionary<string, object>
            {
                ["index"] = index++,
                ["type"] = "relic",
                ["name"] = SafeLocString(() => entry.Model.Title),
                ["description"] = GetRelicDescription(entry.Model),
                ["cost"] = entry.Cost,
                ["affordable"] = entry.EnoughGold
            });
        }

        // Potions
        foreach (var entry in inv.PotionEntries)
        {
            if (!entry.IsStocked) continue;
            items.Add(new Dictionary<string, object>
            {
                ["index"] = index++,
                ["type"] = "potion",
                ["name"] = SafeLocString(() => entry.Model.Title),
                ["description"] = GetPotionDescription(entry.Model),
                ["cost"] = entry.Cost,
                ["affordable"] = entry.EnoughGold
            });
        }

        // Card removal
        try
        {
            var removal = inv.CardRemovalEntry;
            if (removal != null && removal.IsStocked)
            {
                items.Add(new Dictionary<string, object>
                {
                    ["index"] = index++,
                    ["type"] = "card_removal",
                    ["name"] = "Remove Card",
                    ["cost"] = removal.Cost,
                    ["affordable"] = removal.EnoughGold
                });
            }
        }
        catch { }

        result["items"] = items;
        result["gold"] = player.Gold;

        return result;
    }

    private static Dictionary<string, object> SerializeTreasure()
    {
        var result = new Dictionary<string, object>();
        try
        {
            var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            var relics = sync?.CurrentRelics;
            if (relics != null)
            {
                result["relics"] = relics.Select((r, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = SafeLocString(() => r.Title),
                    ["description"] = GetRelicDescription(r)
                }).ToList();
            }
        }
        catch { }

        return result;
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

    private static List<Dictionary<string, object>> GetAvailableCommands()
    {
        var commands = new List<Dictionary<string, object>>();

        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            if (runState == null) return commands;

            // Map screen takes priority over stale overlays
            if (NMapScreen.Instance is { IsOpen: true })
            {
                try
                {
                    var map = runState.Map;
                    var visited = runState.VisitedMapCoords;
                    var points = visited.Count == 0
                        ? map.GetPointsInRow(0).ToList()
                        : map.GetPoint(visited[visited.Count - 1])?.Children.ToList()
                          ?? new List<MapPoint>();

                    for (int i = 0; i < points.Count; i++)
                    {
                        commands.Add(new Dictionary<string, object>
                        {
                            ["type"] = "select_map_node",
                            ["index"] = i,
                            ["nodeType"] = points[i].PointType.ToString()
                        });
                    }
                }
                catch { }
                return commands;
            }

            // Overlay commands take priority (card selection, rewards)
            var overlayScreen = NOverlayStack.Instance?.Peek();

            if (overlayScreen is Node overlayNode)
            {
                var cardHolders = UiHelper.FindAll<NCardHolder>(overlayNode);
                if (cardHolders.Count > 0)
                {
                    // Card selection overlay
                    for (int i = 0; i < cardHolders.Count; i++)
                    {
                        var card = cardHolders[i].CardNode?.Model;
                        if (card != null)
                        {
                            commands.Add(new Dictionary<string, object>
                            {
                                ["type"] = "select_card",
                                ["cardIndex"] = i,
                                ["card"] = card.Title.ToString()
                            });
                        }
                    }

                    var canSkip = overlayScreen is NCardRewardSelectionScreen
                        || UiHelper.FindFirst<NChoiceSelectionSkipButton>(overlayNode) != null;
                    if (canSkip)
                        commands.Add(new Dictionary<string, object> { ["type"] = "skip" });

                    return commands;
                }
            }

            if (overlayScreen is NRewardsScreen rewardsScreen)
            {
                var buttons = UiHelper.FindAll<NRewardButton>((Node)rewardsScreen)
                    .Where(b => b.IsEnabled && b.Reward != null)
                    .ToList();

                for (int i = 0; i < buttons.Count; i++)
                {
                    commands.Add(new Dictionary<string, object>
                    {
                        ["type"] = "select_reward",
                        ["rewardIndex"] = i,
                        ["reward"] = SafeLocString(() => buttons[i].Reward!.Description)
                    });
                }

                // Search within the rewards screen node, not sceneRoot,
                // because other rooms (combat, treasure, etc.) also have NProceedButton instances
                var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
                if (proceedButton?.IsEnabled == true)
                    commands.Add(new Dictionary<string, object> { ["type"] = "proceed" });

                return commands;
            }

            // Hand card selection mode (discard, exhaust, etc.)
            var hand = NPlayerHand.Instance;
            if (hand != null && hand.IsInCardSelection)
            {
                var visibleHolders = hand.CardHolderContainer.GetChildren()
                    .OfType<NHandCardHolder>()
                    .Where(h => h.Visible && h.CardNode?.Model != null)
                    .ToList();

                for (int i = 0; i < visibleHolders.Count; i++)
                {
                    var card = visibleHolders[i].CardNode!.Model;
                    commands.Add(new Dictionary<string, object>
                    {
                        ["type"] = "choose_hand_cards",
                        ["cardIndex"] = i,
                        ["card"] = card.Title.ToString()
                    });
                }

                // Show confirm_selection when the confirm button is enabled
                var confirmField = typeof(NPlayerHand).GetField("_selectModeConfirmButton",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (confirmField != null)
                {
                    var confirmButton = confirmField.GetValue(hand) as NConfirmButton;
                    if (confirmButton != null && confirmButton.IsEnabled)
                        commands.Add(new Dictionary<string, object> { ["type"] = "confirm_selection" });
                }

                return commands;
            }

            // Combat commands
            var cm = CombatManager.Instance;
            if (cm != null && cm.IsInProgress && cm.IsPlayPhase && !cm.PlayerActionsDisabled)
            {
                var player = runState.Players[0];
                var pcs = player.PlayerCombatState;
                if (pcs != null)
                {
                    // Playable cards
                    for (int i = 0; i < pcs.Hand.Cards.Count; i++)
                    {
                        var card = pcs.Hand.Cards[i];
                        if (card.CanPlay())
                        {
                            commands.Add(new Dictionary<string, object>
                            {
                                ["type"] = "play_card",
                                ["cardIndex"] = i,
                                ["card"] = card.Title.ToString(),
                                ["requiresTarget"] = card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly
                            });
                        }
                    }

                    commands.Add(new Dictionary<string, object> { ["type"] = "end_turn" });

                    // Usable potions
                    for (int i = 0; i < player.PotionSlots.Count; i++)
                    {
                        var potion = player.PotionSlots[i];
                        if (potion != null)
                        {
                            commands.Add(new Dictionary<string, object>
                            {
                                ["type"] = "use_potion",
                                ["slot"] = i,
                                ["potion"] = SafeLocString(() => potion.Title)
                            });
                        }
                    }
                }

                return commands;
            }

            // Room-based commands
            var room = runState.CurrentRoom;

            if (room is EventRoom eventRoom)
            {
                var evt = eventRoom.LocalMutableEvent;
                if (evt != null)
                {
                    if (evt.IsFinished)
                    {
                        // Event is done — the UI shows a synthetic Proceed button
                        commands.Add(new Dictionary<string, object>
                        {
                            ["type"] = "proceed"
                        });
                    }
                    else
                    {
                        for (int i = 0; i < evt.CurrentOptions.Count; i++)
                        {
                            var opt = evt.CurrentOptions[i];
                            if (!opt.IsLocked)
                            {
                                commands.Add(new Dictionary<string, object>
                                {
                                    ["type"] = "select_event_option",
                                    ["optionIndex"] = i,
                                    ["label"] = SafeLocString(() => opt.Title)
                                });
                            }
                        }
                    }
                }
            }
            else if (room is RestSiteRoom restRoom)
            {
                foreach (var opt in restRoom.Options)
                {
                    if (opt.IsEnabled)
                    {
                        commands.Add(new Dictionary<string, object>
                        {
                            ["type"] = "rest_option",
                            ["option"] = opt.OptionId,
                            ["name"] = SafeLocString(() => opt.Title)
                        });
                    }
                }
            }
            else if (room is MerchantRoom shopRoom)
            {
                var nRoom = MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom.Instance;
                bool shopIsOpen = nRoom?.Inventory?.IsOpen == true;

                if (!shopIsOpen)
                {
                    // Inventory not yet visible - need to click shopkeeper first
                    commands.Add(new Dictionary<string, object> { ["type"] = "shop_open" });
                    commands.Add(new Dictionary<string, object> { ["type"] = "shop_leave" });
                }
                else
                {
                    var inv = shopRoom.Inventory;
                    if (inv != null)
                    {
                        int idx = 0;
                        foreach (var entry in inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries))
                        {
                            if (entry.IsStocked)
                            {
                                if (entry.EnoughGold)
                                {
                                    commands.Add(new Dictionary<string, object>
                                    {
                                        ["type"] = "shop_buy",
                                        ["itemIndex"] = idx,
                                        ["name"] = entry.CreationResult.Card.Title.ToString()
                                    });
                                }
                                idx++;
                            }
                        }
                        foreach (var entry in inv.RelicEntries)
                        {
                            if (entry.IsStocked)
                            {
                                if (entry.EnoughGold)
                                {
                                    commands.Add(new Dictionary<string, object>
                                    {
                                        ["type"] = "shop_buy",
                                        ["itemIndex"] = idx,
                                        ["name"] = SafeLocString(() => entry.Model.Title)
                                    });
                                }
                                idx++;
                            }
                        }
                        foreach (var entry in inv.PotionEntries)
                        {
                            if (entry.IsStocked)
                            {
                                if (entry.EnoughGold)
                                {
                                    commands.Add(new Dictionary<string, object>
                                    {
                                        ["type"] = "shop_buy",
                                        ["itemIndex"] = idx,
                                        ["name"] = SafeLocString(() => entry.Model.Title)
                                    });
                                }
                                idx++;
                            }
                        }
                        try
                        {
                            var removal = inv.CardRemovalEntry;
                            if (removal != null && removal.IsStocked)
                            {
                                if (removal.EnoughGold)
                                {
                                    commands.Add(new Dictionary<string, object>
                                    {
                                        ["type"] = "shop_buy",
                                        ["itemIndex"] = idx,
                                        ["name"] = "Remove Card"
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                    commands.Add(new Dictionary<string, object> { ["type"] = "shop_leave" });
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log($"Error building available commands: {e}");
        }

        return commands;
    }

    private static Dictionary<string, object> SerializeHandSelection(NPlayerHand hand)
    {
        var overlay = new Dictionary<string, object>
        {
            ["type"] = "hand_select"
        };

        // Get prompt and min/max from _prefs via reflection
        var prefsField = typeof(NPlayerHand).GetField("_prefs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (prefsField != null)
        {
            try
            {
                dynamic prefs = prefsField.GetValue(hand)!;
                overlay["prompt"] = StripBBCode(((LocString)prefs.Prompt).GetFormattedText());
                overlay["minSelect"] = (int)prefs.MinSelect;
                overlay["maxSelect"] = (int)prefs.MaxSelect;
            }
            catch { }
        }

        // Get currently selected cards
        var selectedField = typeof(NPlayerHand).GetField("_selectedCards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (selectedField != null)
        {
            try
            {
                var selected = (List<CardModel>)selectedField.GetValue(hand)!;
                overlay["selectedCount"] = selected.Count;
            }
            catch { }
        }

        // Get available (visible) cards in hand that can be selected
        var holders = hand.CardHolderContainer.GetChildren()
            .OfType<NHandCardHolder>()
            .Where(h => h.Visible && h.CardNode?.Model != null)
            .Select((h, i) =>
            {
                var card = h.CardNode!.Model;
                var result = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = card.Title.ToString(),
                    ["description"] = GetCardDescription(card)
                };
                return result;
            })
            .ToList();

        overlay["cards"] = holders;
        return overlay;
    }

    private static Dictionary<string, object>? DetectOverlay()
    {
        var sceneRoot = GetSceneRoot();
        if (sceneRoot == null) return null;

        // Check for hand card selection mode (discard, exhaust, etc.)
        var hand = NPlayerHand.Instance;
        if (hand != null && hand.IsInCardSelection)
        {
            return SerializeHandSelection(hand);
        }

        // Use the overlay stack to find the current overlay screen
        var overlayScreen = NOverlayStack.Instance?.Peek();
        var overlayNode = overlayScreen as Node;

        // Check for card selection overlay (card picks from rewards, card removal, upgrade, etc.)
        // Search within the overlay screen to avoid finding hand card holders
        var cardHolders = overlayNode != null
            ? UiHelper.FindAll<NCardHolder>(overlayNode)
            : new List<NCardHolder>();
        if (cardHolders.Count > 0)
        {
            var cards = cardHolders
                .Select((h, i) =>
                {
                    var cardNode = h.CardNode;
                    var card = cardNode?.Model;
                    if (card == null) return null;
                    var result = new Dictionary<string, object>
                    {
                        ["index"] = i,
                        ["name"] = card.Title,
                        ["description"] = GetCardDescriptionFromNode(cardNode!) ?? GetCardDescription(card)
                    };
                    if (card.EnergyCost != null)
                    {
                        if (card.EnergyCost.CostsX)
                            result["cost"] = "X";
                        else
                            result["cost"] = card.EnergyCost.Canonical;
                    }
                    return result;
                })
                .Where(c => c != null)
                .ToList();

            if (cards.Count > 0)
            {
                var overlay = new Dictionary<string, object>
                {
                    ["type"] = "card_selection",
                    ["cards"] = cards
                };
                // NCardRewardSelectionScreen doesn't use NChoiceSelectionSkipButton —
                // the player can always dismiss back to the rewards screen.
                // NChooseACardSelectionScreen has an explicit skip button.
                var canSkip = overlayScreen is NCardRewardSelectionScreen;
                if (!canSkip && overlayNode != null)
                    canSkip = UiHelper.FindFirst<NChoiceSelectionSkipButton>(overlayNode) != null;
                overlay["canSkip"] = canSkip;

                // Report min/max select count for screens that require multiple picks
                // (NDeckCardSelectScreen, NSimpleCardSelectScreen have a _prefs field)
                var prefsField = overlayScreen?.GetType().GetField("_prefs",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prefsField != null)
                {
                    try
                    {
                        dynamic prefs = prefsField.GetValue(overlayScreen)!;
                        overlay["minSelect"] = (int)prefs.MinSelect;
                        overlay["maxSelect"] = (int)prefs.MaxSelect;
                    }
                    catch { /* ignore if reflection fails */ }
                }

                return overlay;
            }
        }

        // Check for rewards screen overlay
        if (overlayScreen is NRewardsScreen rewardsScreen)
        {
            var buttons = UiHelper.FindAll<NRewardButton>((Node)rewardsScreen)
                .Where(b => b.IsEnabled && b.Reward != null)
                .Select((b, i) =>
                {
                    var reward = b.Reward!;
                    var entry = new Dictionary<string, object>
                    {
                        ["index"] = i,
                        ["type"] = reward switch
                        {
                            GoldReward => "gold",
                            CardReward => "card",
                            PotionReward => "potion",
                            RelicReward => "relic",
                            CardRemovalReward => "card_removal",
                            _ => "unknown"
                        },
                        ["description"] = SafeLocString(() => reward.Description)
                    };
                    return entry;
                })
                .ToList();

            var overlay = new Dictionary<string, object>
            {
                ["type"] = "rewards",
                ["rewards"] = buttons
            };
            // Search within the rewards screen, not sceneRoot, to find the correct proceed button
            var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
            overlay["canProceed"] = proceedButton?.IsEnabled ?? false;
            return overlay;
        }

        return null;
    }

    /// <summary>
    /// Read the already-rendered description text from an NCard's label node.
    /// This works even outside combat where GetDescriptionForPile would throw.
    /// </summary>
    private static string? GetCardDescriptionFromNode(NCard cardNode)
    {
        try
        {
            var label = cardNode.GetNode<Godot.RichTextLabel>("%DescriptionLabel");
            if (label == null) return null;
            var text = label.Text;
            if (string.IsNullOrEmpty(text)) return null;
            return StripBBCode(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get a card's description with dynamic variables resolved.
    /// </summary>
    private static string GetCardDescription(CardModel card)
    {
        try
        {
            LocString desc = card.Description;
            card.DynamicVars.AddTo(desc);
            desc.Add("OnTable", true);
            desc.Add("InCombat", CombatManager.Instance?.IsInProgress ?? false);
            return StripBBCode(desc.GetFormattedText());
        }
        catch
        {
            return SafeLocString(() => card.Description);
        }
    }

    /// <summary>
    /// Get a relic's description with dynamic variables resolved.
    /// </summary>
    private static string GetRelicDescription(RelicModel relic)
    {
        try
        {
            return StripBBCode(relic.DynamicDescription.GetFormattedText());
        }
        catch
        {
            return SafeLocString(() => relic.Description);
        }
    }

    /// <summary>
    /// Get a potion's description with dynamic variables resolved.
    /// </summary>
    private static string GetPotionDescription(PotionModel potion)
    {
        try
        {
            return StripBBCode(potion.DynamicDescription.GetFormattedText());
        }
        catch
        {
            return SafeLocString(() => potion.Description);
        }
    }

    /// <summary>
    /// Get a power's description with dynamic variables resolved.
    /// </summary>
    private static string GetPowerDescription(PowerModel power)
    {
        try
        {
            LocString desc = power.Description;
            power.DynamicVars.AddTo(desc);
            desc.Add("Amount", power.Amount);
            return StripBBCode(desc.GetFormattedText());
        }
        catch
        {
            return "???";
        }
    }

    private static string SafeLocString(Func<object> getter)
    {
        try
        {
            var val = getter();
            if (val is LocString loc)
                return StripBBCode(loc.GetFormattedText());
            return StripBBCode(val?.ToString() ?? "");
        }
        catch
        {
            return "???";
        }
    }

    private static readonly Regex ImgTagRegex = new(@"\[img[^\]]*\](.*?)\[/img\]", RegexOptions.Compiled);
    private static readonly Regex BbCodeRegex = new(@"\[/?[^\]]+\]", RegexOptions.Compiled);

    private static string GetLocalizedIconText(string iconPath)
    {
        try
        {
            var table = LocManager.Instance.GetTable("static_hover_tips");
            if (iconPath.Contains("energy_icon"))
                return table.GetRawText("ENERGY.title");
            if (iconPath.Contains("star_icon"))
                return table.GetRawText("STAR_COUNT.title");
        }
        catch { }
        return "";
    }

    /// <summary>
    /// Replace [img]...[/img] with localized text, then strip remaining BBCode tags.
    /// </summary>
    private static string StripBBCode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = ImgTagRegex.Replace(text, match => GetLocalizedIconText(match.Groups[1].Value));

        return BbCodeRegex.Replace(text, "").Trim();
    }
}

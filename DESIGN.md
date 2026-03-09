# STS2 Agent Mod

## Architecture

```
┌─────────────────────────────────────┐
│  Agent (external process)           │
│  - Polls for game state             │
│  - Sends actions as JSON            │
└──────────────┬──────────────────────┘
               │ HTTP (localhost:8080)
┌──────────────▼──────────────────────┐
│  Agent Bridge (HarmonyX mod)        │
│  - Serializes game state            │
│  - Executes actions via game UI     │
│  - Detects stable decision points   │
└──────────────┬──────────────────────┘
               │ HarmonyX patches
┌──────────────▼──────────────────────┐
│  STS2 Game (sts2.dll / Godot)       │
└─────────────────────────────────────┘
```

The mod follows the game's built-in **AutoSlay** bot patterns. State is read via `RunManager.Instance.DebugOnlyGetState()` and `CombatManager.Instance`. Actions are executed through the game's own UI path — `TryManualPlay` for cards, `ForceClick` for buttons, `EmitSignal` for card selection — all marshalled to Godot's main thread via `CallDeferred`.

## Mod Files

| File | Purpose |
|---|---|
| `Plugin.cs` | Entry point. Applies HarmonyX patches, starts HTTP server, wires stability events. |
| `Patches.cs` | Minimal HarmonyX patches: combat setup, overlay push/remove, event state changes. |
| `GameStabilityDetector.cs` | Monitors game state and fires `OnBecameStable` when the game is waiting for player input. |
| `GameStateSerializer.cs` | Reads game state and produces JSON. |
| `HttpServer.cs` | HTTP server on port 8080 with three endpoints. |
| `ActionExecutor.cs` | Parses action JSON and executes it through the game's UI. |

## HTTP API

### `GET /state`

Returns the current game state JSON immediately.

### `GET /state/wait?timeout=30000`

Long-polls until a decision point is reached (game becomes stable and waiting for player input). Returns `{"timeout": true}` if the timeout expires. Default timeout: 30s, max: 120s.

### `POST /action`

Accepts an action JSON body. Executes the action, waits up to 10s for the game to stabilize, then returns:

```json
{"status": "ok", "message": "Card played", "stable": true, "state": { ... }}
```

On error:
```json
{"error": "Card index 5 out of range (hand size: 4)"}
```

The `state` field in successful responses contains the full game state after the action, so the agent doesn't need a separate `/state` call after each action.

## Game State Schema

The top-level state always contains `context`, `player`, and `available_commands`. Context-specific fields and `overlay` are present when applicable.

### Top level

```json
{
  "context": "combat | map | event | shop | rest | treasure | unknown",
  "player": { ... },
  "combat": { ... },
  "map": { ... },
  "event": { ... },
  "shop": { ... },
  "rest": { ... },
  "treasure": { ... },
  "overlay": { ... },
  "available_commands": [ ... ]
}
```

### `player`

Always present during an active run.

```json
{
  "hp": 70, "maxHp": 80, "gold": 120,
  "relics": [{ "name": "Burning Blood", "description": "At the end of combat, heal 6 HP." }],
  "potions": [{ "slot": 0, "name": "Fire Potion", "description": "Deal 20 damage." }],
  "deck": [{ "name": "Strike", "description": "Deal 6 damage.", "cost": 1 }]
}
```

Potions only include non-empty slots. Deck cards include `cost` (int or `"X"`).

### `combat`

Present when `context` is `"combat"`.

```json
{
  "round": 1,
  "currentSide": "Player",
  "energy": 3,
  "stars": 0,
  "hand": [
    { "index": 0, "name": "Strike", "description": "Deal 6 damage.",
      "cost": 1, "targetType": "AnyEnemy", "playable": true }
  ],
  "drawPileCount": 5,
  "discardPileCount": 0,
  "exhaustPileCount": 0,
  "playerBlock": 0,
  "playerPowers": [{ "name": "Strength", "amount": 2, "description": "..." }],
  "enemies": [
    { "index": 0, "name": "Jaw Worm", "hp": 42, "maxHp": 42, "block": 0,
      "powers": [],
      "intents": [{ "type": "Attack", "damage": 11 }] }
  ]
}
```

- `targetType`: `AnyEnemy`, `AnyAlly`, `All`, `None`, etc.
- `intents`: list because enemies can have multi-part turns. Attack intents include `damage` and optionally `hits` (when > 1).
- Only alive enemies are included.

### `map`

Present when `context` is `"map"` or `"rest"`.

```json
{
  "currentCoord": { "row": 3, "col": 1 },
  "act": 1,
  "availableNodes": [
    { "coord": { "row": 4, "col": 0 }, "type": "Combat" },
    { "coord": { "row": 4, "col": 2 }, "type": "Treasure" }
  ]
}
```

### `event`

Present when `context` is `"event"`.

```json
{
  "title": "Big Fish",
  "description": "You see a giant fish...",
  "options": [
    { "index": 0, "label": "Feed", "locked": false },
    { "index": 1, "label": "Run", "locked": false }
  ]
}
```

### `rest`

Present when `context` is `"rest"`.

```json
{
  "options": [
    { "index": 0, "id": "rest", "name": "Rest", "description": "Heal 30% of your max HP.", "enabled": true },
    { "index": 1, "id": "smith", "name": "Smith", "description": "Upgrade a card.", "enabled": true }
  ]
}
```

### `shop`

Present when `context` is `"shop"`.

```json
{
  "items": [
    { "index": 0, "type": "card", "name": "Headbutt", "description": "...", "cost": 75, "affordable": true },
    { "index": 1, "type": "relic", "name": "Vajra", "description": "...", "cost": 150, "affordable": false },
    { "index": 2, "type": "potion", "name": "Block Potion", "description": "...", "cost": 50, "affordable": true },
    { "index": 3, "type": "card_removal", "name": "Remove Card", "cost": 75, "affordable": true }
  ],
  "gold": 120
}
```

### `treasure`

Present when `context` is `"treasure"`.

```json
{
  "relics": [
    { "index": 0, "name": "Vajra", "description": "At the start of each combat, gain 1 Strength." }
  ]
}
```

### `overlay`

Present when a modal screen (rewards, card selection) is shown on top of any context.

**Rewards overlay:**
```json
{
  "type": "rewards",
  "rewards": [
    { "index": 0, "type": "gold", "description": "25 Gold" },
    { "index": 1, "type": "card", "description": "Card Reward" },
    { "index": 2, "type": "potion", "description": "Fire Potion" }
  ],
  "canProceed": true
}
```

**Card selection overlay:**
```json
{
  "type": "card_selection",
  "cards": [
    { "index": 0, "name": "Headbutt", "description": "...", "cost": 1 },
    { "index": 1, "name": "Anger", "description": "...", "cost": 0 }
  ],
  "canSkip": true
}
```

### `available_commands`

A list of actions the agent can take right now. Each entry has a `type` plus context-specific fields. Overlay commands take priority when an overlay is active.

**Combat:**
```json
[
  { "type": "play_card", "cardIndex": 0, "card": "Strike", "requiresTarget": true },
  { "type": "play_card", "cardIndex": 2, "card": "Defend", "requiresTarget": false },
  { "type": "end_turn" },
  { "type": "use_potion", "slot": 0, "potion": "Fire Potion" }
]
```

Only playable cards are listed. Unplayable cards (not enough energy, etc.) are omitted.

**Map:**
```json
[
  { "type": "select_map_node", "coord": { "row": 4, "col": 0 }, "nodeType": "Combat" },
  { "type": "select_map_node", "coord": { "row": 4, "col": 2 }, "nodeType": "Treasure" }
]
```

**Event:**
```json
[
  { "type": "select_event_option", "optionIndex": 0, "label": "Feed" },
  { "type": "select_event_option", "optionIndex": 1, "label": "Run" }
]
```

**Rest site:**
```json
[
  { "type": "rest_option", "option": "rest", "name": "Rest" },
  { "type": "rest_option", "option": "smith", "name": "Smith" }
]
```

**Shop:**
```json
[
  { "type": "shop_buy", "itemIndex": 0, "name": "Headbutt" },
  { "type": "shop_buy", "itemIndex": 2, "name": "Block Potion" },
  { "type": "shop_leave" }
]
```

Only affordable items are listed.

**Rewards overlay:**
```json
[
  { "type": "select_reward", "rewardIndex": 0, "reward": "25 Gold" },
  { "type": "select_reward", "rewardIndex": 1, "reward": "Card Reward" },
  { "type": "proceed" }
]
```

**Card selection overlay:**
```json
[
  { "type": "select_card", "cardIndex": 0, "card": "Headbutt" },
  { "type": "select_card", "cardIndex": 1, "card": "Anger" },
  { "type": "skip" }
]
```

## Action Schema

```json
{ "type": "play_card",           "cardIndex": 0, "targetIndex": 1 }
{ "type": "end_turn" }
{ "type": "use_potion",          "slot": 0, "targetIndex": 1 }
{ "type": "select_map_node",     "coord": { "row": 4, "col": 0 } }
{ "type": "select_event_option", "optionIndex": 0 }
{ "type": "select_card",         "cardIndex": 0 }
{ "type": "skip" }
{ "type": "select_reward",       "rewardIndex": 0 }
{ "type": "rest_option",         "option": "rest" }
{ "type": "shop_buy",            "itemIndex": 0 }
{ "type": "shop_leave" }
{ "type": "proceed" }
```

Notes:
- `play_card`: `targetIndex` required when `targetType` is `AnyEnemy` or `AnyAlly`. Index into the alive enemies list. If omitted for a targeted card, defaults to first valid target.
- `rest_option`: `option` is matched by option ID (e.g., `"rest"`, `"smith"`, `"recall"`). Falls back to numeric index.
- `shop_buy`: `itemIndex` matches the flat `items` array in the shop state.

## Stability Detection

`GameStabilityDetector` determines when the game is waiting for player input. It fires `OnBecameStable`, which unblocks `/state/wait` and post-action stability waits.

**Triggers:**
- `CombatManager.SetUpCombat` (patch) — subscribes to combat events
- `CombatManager.TurnStarted`, `PlayerActionsDisabledChanged` — combat state changes
- `CombatManager.CombatEnded` — unsubscribes from combat
- `NOverlayStack.Push` / `Remove` (patches) — overlay screen changes
- `EventModel.SetEventState` (patch) — event option changes
- `RunManager.RoomEntered` (event) — room transitions
- `ActionExecutor.BeforeActionExecuted` / `AfterActionExecuted` — action queue events

**Stable conditions:**
- **Combat:** play phase, player actions not disabled, no action currently running
- **Overlay present:** any overlay screen (rewards, card selection) counts as stable
- **Interactive room:** `MapRoom`, `RestSiteRoom`, `MerchantRoom`, `TreasureRoom`
- **Event room:** event has options available

**Flow:**
1. A trigger fires → `ScheduleStabilityCheck()` queues a deferred check
2. `CheckStability()` runs on the main thread, evaluates `IsStable()`
3. If stable and wasn't stable before → fires `OnBecameStable`
4. `HttpServer.SignalDecisionPoint()` unblocks waiting requests

## Threading Model

The HTTP server runs on a background thread. Game state must be read and actions must be executed on Godot's main thread.

- **State reads** (`GameStateSerializer.Serialize`) access game objects directly. These are called from the HTTP thread but read stable snapshots.
- **Actions** are marshalled to the main thread via `Callable.From(...).CallDeferred()`:
  - Card plays: `card.TryManualPlay(target)` — same path as the game's UI drag-and-drop
  - End turn: `ActionQueueSynchronizer.RequestEnqueue(EndPlayerTurnAction)` — same as the end turn button
  - Button clicks: `button.ForceClick()` — for map nodes, events, rewards, rest, shop
  - Card selection: `holder.EmitSignal(Pressed, holder)` — same as AutoSlay's CardRewardScreenHandler
  - Potion use: `potion.EnqueueManualUse(target)`
- After executing an action, the HTTP handler waits up to 10s for `OnBecameStable` before returning the response.

## Logging

All activity is logged to `~/sts2agent.log` (UTF-8 with BOM). The stability detector logs full serialized state at every decision point. Action requests and results are logged by the HTTP server.

## Key Reference Files (Decompiled)

| Area | File |
|---|---|
| Game loop | `MegaCrit.Sts2.Core.Combat/CombatManager.cs` |
| Run state | `MegaCrit.Sts2.Core.Runs/RunManager.cs`, `RunState.cs` |
| Combat state | `MegaCrit.Sts2.Core.Combat/CombatState.cs` |
| Player state | `MegaCrit.Sts2.Core.Entities.Players/Player.cs`, `PlayerCombatState.cs` |
| Creatures | `MegaCrit.Sts2.Core.Entities.Creatures/Creature.cs` |
| Cards | `MegaCrit.Sts2.Core.Models/CardModel.cs`, `MegaCrit.Sts2.Core.Commands/CardCmd.cs` |
| Monsters/intents | `MegaCrit.Sts2.Core.Models/MonsterModel.cs`, `MegaCrit.Sts2.Core.MonsterMoves.Intents/AbstractIntent.cs` |
| Actions | `MegaCrit.Sts2.Core.GameActions/PlayCardAction.cs`, `EndPlayerTurnAction.cs` |
| AutoSlay combat | `MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms/CombatRoomHandler.cs` |
| AutoSlay map | `MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens/MapScreenHandler.cs` |
| AutoSlay events | `MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms/EventRoomHandler.cs` |
| UI helpers | `MegaCrit.Sts2.Core.AutoSlay.Helpers/UiHelper.cs`, `WaitHelper.cs` |
| Map | `MegaCrit.Sts2.Core.Map/ActMap.cs`, `MapPoint.cs`, `MapCoord.cs` |
| Events | `MegaCrit.Sts2.Core.Models/EventModel.cs`, `MegaCrit.Sts2.Core.Events/EventOption.cs` |
| Overlay stack | `MegaCrit.Sts2.Core.Nodes.Screens.Overlays/NOverlayStack.cs` |

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
│  - Logs gameplay events             │
└──────────────┬──────────────────────┘
               │ HarmonyX patches
┌──────────────▼──────────────────────┐
│  STS2 Game (sts2.dll / Godot)       │
└─────────────────────────────────────┘
```

The mod follows the game's built-in **AutoSlay** bot patterns. State is read via `RunManager.Instance.DebugOnlyGetState()` and `CombatManager.Instance`. Actions are executed through the game's own UI path — `TryManualPlay` for cards, `ForceClick` for buttons, `EmitSignal` for card selection — all marshalled to Godot's main thread via `CallDeferred`.

A **context-handler pattern** routes all serialization and action execution through `IContextHandler` implementations. `GameContext.Resolve()` is the single source of truth for the current interaction context, returning a `ContextInfo` with the active context type and relevant game objects.

## Mod Files

| File | Purpose |
|---|---|
| `Plugin.cs` | Entry point. Applies HarmonyX patches, starts HTTP server, wires stability events. |
| `Patches.cs` | HarmonyX patches: combat setup, overlay push/remove, event state changes, map/room/menu transitions. |
| `EventPatches.cs` | HarmonyX patches that log gameplay events (damage, death, powers, cards, relics). |
| `EventLog.cs` | Thread-safe event queue. Accumulates `GameEvent` entries drained on each state read. |
| `GameStabilityDetector.cs` | Monitors game state and fires `OnBecameStable` when the game is waiting for player input. |
| `GameStateSerializer.cs` | Reads game state and produces JSON. Delegates to context handlers for context-specific data. |
| `MapSerializer.cs` | Dedicated full-map serializer for the `/map` endpoint (all nodes, edges, visited/boss coords). |
| `HttpServer.cs` | HTTP server on port 8080 with five endpoints. |
| `ActionExecutor.cs` | Routes action JSON to the appropriate `IContextHandler.TryExecute`. |

### Context Handlers (`Contexts/`)

| File | Context | Purpose |
|---|---|---|
| `GameContext.cs` | — | Resolves current context with priority: Map > Overlays > Combat > Room > Pre-run. |
| `IContextHandler.cs` | — | Interface: `SerializeState`, `GetCommands`, `TryExecute`. |
| `MapHandler.cs` | Map | Map navigation. Serializes available nodes, executes node selection. |
| `CombatHandler.cs` | Combat | Card play, end turn, potion use. Serializes hand/enemies/powers. |
| `HandSelectionHandler.cs` | HandSelection | In-combat card selection prompts (discard/exhaust). |
| `CardSelectionHandler.cs` | CardSelection | Overlay card selection (card rewards, upgrade picks, grid selections). |
| `RewardsHandler.cs` | Rewards | Post-combat reward selection (gold, cards, potions, relics). |
| `EventHandler.cs` | Event | Event dialogue options. Auto-advances ancient event dialogue. |
| `RestSiteHandler.cs` | RestSite | Rest site options (rest, smith, recall, etc.). |
| `ShopHandler.cs` | Shop | Shop open/browse/buy/leave. |
| `TreasureHandler.cs` | Treasure | Treasure room (chest auto-opened by patch, proceed when ready). |
| `GameOverHandler.cs` | GameOver | Victory/defeat screen, return to menu. |
| `CharacterSelectHandler.cs` | CharacterSelect | Character selection and embark. |
| `MainMenuHandler.cs` | MainMenu | Start run, continue saved run, abandon run. |

### Utilities (`Utilities/`)

| File | Purpose |
|---|---|
| `GodotMainThread.cs` | `RunAsync()` and `ClickAsync()` — marshals work to Godot's main thread via `CallDeferred`. |
| `TextHelper.cs` | Text localization, BBCode stripping, card/relic/potion/power description formatting. |
| `SceneHelper.cs` | Scene tree root access. |
| `ActionResult.cs` | JSON response helpers (`Ok`, `Error`). |
| `ReflectionCache.cs` | Cached `FieldInfo` for private fields (hand selection prefs, confirm button, etc.). |

## Context Resolution Priority

`GameContext.Resolve()` determines the current interaction context in this order:

1. **No active run?** → CharacterSelect (if visible) or MainMenu
2. **Map screen open?** → Map
3. **Overlay stack** (top overlay):
   - GameOver screen → GameOver
   - Overlay with card holders → CardSelection
   - Rewards screen → Rewards
4. **Hand in card selection mode?** → HandSelection (discard/exhaust prompts)
5. **Active combat?** → Combat
6. **Current room type** → Event, RestSite, Shop, or Treasure
7. **Fallback** → Unknown

## HTTP API

### `GET /state`

Returns the current game state JSON immediately.

### `GET /state/wait?timeout=30000`

Long-polls until a decision point is reached (game becomes stable and waiting for player input). Returns `{"timeout": true}` if the timeout expires. Default timeout: 30s, max: 120s.

### `GET /map`

Returns the full map layout for the current act: all nodes with types, coordinates, parent-child edges, visited status, boss positions, and starting nodes. Available during an active run regardless of context.

```json
{
  "act": 1,
  "rows": 15,
  "cols": 7,
  "currentCoord": { "row": 3, "col": 1 },
  "visitedCoords": [{ "row": 0, "col": 3 }, { "row": 1, "col": 2 }],
  "bossCoord": { "row": 15, "col": 3 },
  "startCoords": [{ "row": 0, "col": 1 }, { "row": 0, "col": 3 }],
  "nodes": [
    { "coord": { "row": 0, "col": 3 }, "type": "Combat", "children": [...], "visited": true }
  ]
}
```

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

### `GET /log-level?level=debug`

Gets or sets the log level. Valid levels: `debug`, `info`, `error`. Without `level` parameter, returns the current level.

## Game State Schema

The top-level state always contains `context` and `available_commands`. When a run is active, `player` is present. Context-specific fields and `overlay` are present when applicable. Gameplay `events` are included when accumulated since the last state read.

### Top level

```json
{
  "context": "combat | map | event | shop | rest | treasure | game_over | character_select | main_menu | unknown",
  "player": { ... },
  "combat": { ... },
  "map": { ... },
  "event": { ... },
  "shop": { ... },
  "rest": { ... },
  "treasure": { ... },
  "overlay": { ... },
  "events": [ ... ],
  "available_commands": [ ... ]
}
```

When an overlay (CardSelection, Rewards, HandSelection) is active, `context` reflects the underlying room type (e.g., `"combat"`) and the overlay state goes in the `overlay` key. The underlying room state is also serialized alongside (e.g., `combat` data is present even when a rewards screen is shown over combat).

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

Present when `context` is `"combat"` or when combat is the underlying room during an overlay.

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
  "orbSlots": 3,
  "orbs": [
    { "index": 0, "name": "Lightning", "passiveValue": 3, "evokeValue": 8 }
  ],
  "playerBlock": 0,
  "playerPowers": [{ "name": "Strength", "amount": 2, "description": "..." }],
  "enemies": [
    { "index": 0, "name": "Jaw Worm", "hp": 42, "maxHp": 42, "block": 0,
      "powers": [],
      "intents": [{ "type": "Attack", "damage": 11 }] }
  ]
}
```

- Card `cost` in hand reflects current modifiers (e.g., after cost reduction).
- `targetType`: `AnyEnemy`, `AnyAlly`, `All`, `None`, etc.
- `intents`: list because enemies can have multi-part turns. Attack intents include `damage` and optionally `hits` (when > 1).
- Only alive enemies are included.
- `orbSlots` and `orbs` only present for Defect (when orb capacity > 0). Values reflect current modifiers.

### `map`

Present when `context` is `"map"` or `"rest"` (rest sites show available next nodes).

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
    { "index": 0, "label": "Feed", "description": "Heal 5 HP.", "locked": false },
    { "index": 1, "label": "Run", "description": "", "locked": false }
  ]
}
```

Event option descriptions are dynamically resolved with the event's `DynamicVars`.

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
  "isOpen": true,
  "gold": 120,
  "items": [
    { "index": 0, "type": "card", "name": "Headbutt", "description": "...", "cost": 75, "affordable": true },
    { "index": 1, "type": "relic", "name": "Vajra", "description": "...", "cost": 150, "affordable": false },
    { "index": 2, "type": "potion", "name": "Block Potion", "description": "...", "cost": 50, "affordable": true },
    { "index": 3, "type": "card_removal", "name": "Remove Card", "cost": 75, "affordable": true }
  ]
}
```

The shop must be opened first (`shop_open`) before items are visible. `isOpen` tracks whether the inventory is displayed. Only stocked (unsold) items are listed.

### `treasure`

Present when `context` is `"treasure"`.

```json
{
  "relics": [
    { "index": 0, "name": "Vajra", "description": "At the start of each combat, gain 1 Strength." }
  ],
  "proceedAvailable": true
}
```

Treasure chests are auto-opened by a patch. The agent waits for `proceedAvailable` then sends `proceed`.

### `game_over`

Present when `context` is `"game_over"`.

```json
{
  "victory": true
}
```

### `character_select`

Present when `context` is `"character_select"` (pre-run, no `player`).

```json
{
  "characters": [
    { "index": 0, "name": "Ironclad", "locked": false },
    { "index": 1, "name": "Silent", "locked": false }
  ],
  "selected": "Ironclad"
}
```

### `main_menu`

Present when `context` is `"main_menu"` (pre-run, no `player`).

```json
{
  "screen": "main_menu",
  "has_saved_run": true
}
```

### `overlay`

Present when a modal screen (rewards, card selection, hand selection) is shown on top of any context.

**Rewards overlay:**
```json
{
  "type": "rewards",
  "rewards": [
    { "index": 0, "type": "gold", "description": "25 Gold" },
    { "index": 1, "type": "card", "description": "Card Reward" },
    { "index": 2, "type": "potion", "description": "Fire Potion" },
    { "index": 3, "type": "card_removal", "description": "Remove a card" }
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
  "canSkip": true,
  "minSelect": 1,
  "maxSelect": 1
}
```

`minSelect`/`maxSelect` are present for multi-pick screens (e.g., grid selections). For single-pick (card rewards), they are omitted.

**Hand selection overlay:**
```json
{
  "type": "hand_select",
  "prompt": "Choose a card to discard.",
  "minSelect": 1,
  "maxSelect": 1,
  "selectedCount": 0,
  "cards": [
    { "index": 0, "name": "Strike", "description": "Deal 6 damage." },
    { "index": 1, "name": "Defend", "description": "Gain 5 Block." }
  ]
}
```

### `events`

Gameplay events accumulated since the last state read. Drained on each `/state` or `/state/wait` response.

```json
[
  {
    "type": "damage_received",
    "message": "Ironclad dealt 6 damage to Jaw Worm (0 blocked, 6 HP lost)",
    "details": {
      "target": "Jaw Worm", "dealer": "Ironclad",
      "total_damage": 6, "blocked": 0, "hp_loss": 6, "was_kill": false
    }
  }
]
```

**Event types:**
| Type | Trigger | Details |
|---|---|---|
| `damage_received` | Any damage dealt | `target`, `dealer`, `total_damage`, `blocked`, `hp_loss`, `was_kill` |
| `creature_died` | Creature death | `creature`, `is_enemy` |
| `card_auto_played` | Auto-play effect | `card`, `target`, `auto_play_type` |
| `power_changed` | Power applied/removed | `owner`, `power`, `amount`, `new_amount`, `applier`, `type` |
| `card_upgraded` | Card upgrade | `old_name`, `new_name` |
| `card_removed` | Card removed from deck | `card` |
| `relic_activated` | Relic flashes (triggers) | `relic` |
| `relic_obtained` | Relic added | `relic` |
| `relic_removed` | Relic lost | `relic` |

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
  { "type": "select_map_node", "index": 0, "nodeType": "Combat", "coord": { "row": 4, "col": 0 } },
  { "type": "select_map_node", "index": 1, "nodeType": "Treasure", "coord": { "row": 4, "col": 2 } }
]
```

Map nodes are selected by `index` (position in the available nodes list).

**Event:**
```json
[
  { "type": "select_event_option", "optionIndex": 0, "label": "Feed" },
  { "type": "select_event_option", "optionIndex": 1, "label": "Run" }
]
```

When the event is finished, a `proceed` command replaces the options.

**Rest site:**
```json
[
  { "type": "rest_option", "option": "rest", "name": "Rest" },
  { "type": "rest_option", "option": "smith", "name": "Smith" },
  { "type": "proceed" }
]
```

`proceed` appears after selecting an option and waiting for the animation to complete.

**Shop:**
```json
[
  { "type": "shop_open" },
  { "type": "shop_buy", "itemIndex": 0, "name": "Headbutt" },
  { "type": "shop_buy", "itemIndex": 2, "name": "Block Potion" },
  { "type": "shop_leave" }
]
```

`shop_open` appears when the inventory is not yet displayed. Only affordable items get `shop_buy` commands.

**Treasure:**
```json
[
  { "type": "proceed" }
]
```

Chest is auto-opened. `proceed` appears when relics have been collected.

**Game over:**
```json
[
  { "type": "return_to_menu" }
]
```

**Character select:**
```json
[
  { "type": "select_character", "index": 0, "name": "Ironclad" },
  { "type": "select_character", "index": 1, "name": "Silent" },
  { "type": "embark" }
]
```

Only unlocked characters are listed. `embark` starts the run with the selected character.

**Main menu:**
```json
[
  { "type": "continue_run" },
  { "type": "abandon_run" }
]
```

Or when no saved run exists:
```json
[
  { "type": "start_run" }
]
```

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

**Hand selection overlay:**
```json
[
  { "type": "choose_hand_cards", "cardIndex": 0, "card": "Strike" },
  { "type": "choose_hand_cards", "cardIndex": 1, "card": "Defend" },
  { "type": "confirm_selection" }
]
```

`confirm_selection` only appears when enough cards have been selected (per `minSelect`).

## Action Schema

```json
{ "type": "play_card",           "cardIndex": 0, "targetIndex": 1 }
{ "type": "end_turn" }
{ "type": "use_potion",          "slot": 0, "targetIndex": 1 }
{ "type": "select_map_node",     "index": 0 }
{ "type": "select_event_option", "optionIndex": 0 }
{ "type": "select_card",         "cardIndex": 0 }
{ "type": "skip" }
{ "type": "select_reward",       "rewardIndex": 0 }
{ "type": "rest_option",         "option": "rest" }
{ "type": "shop_open" }
{ "type": "shop_buy",            "itemIndex": 0 }
{ "type": "shop_leave" }
{ "type": "proceed" }
{ "type": "choose_hand_cards",   "cardIndex": 0 }
{ "type": "confirm_selection" }
{ "type": "return_to_menu" }
{ "type": "select_character",    "index": 0 }
{ "type": "embark" }
{ "type": "continue_run" }
{ "type": "abandon_run" }
{ "type": "start_run" }
```

Notes:
- `play_card`: `targetIndex` required when `targetType` is `AnyEnemy` or `AnyAlly`. Index into the alive enemies list. If omitted for a targeted card, defaults to first valid target.
- `select_map_node`: `index` is the position in the `availableNodes` list (not a coordinate).
- `rest_option`: `option` is matched by option ID (e.g., `"rest"`, `"smith"`, `"recall"`). Falls back to numeric index.
- `shop_buy`: `itemIndex` matches the flat `items` array in the shop state.
- `shop_open`: Opens the shop inventory. Required before browsing/buying.
- `choose_hand_cards`: Toggles a card in the hand selection. May need multiple calls for multi-select.
- `confirm_selection`: Confirms the hand card selection after enough cards are chosen.
- `start_run`: Navigates through singleplayer → standard mode to character select.
- `embark`: Starts the run with the currently selected character.

## Stability Detection

`GameStabilityDetector` determines when the game is waiting for player input. It fires `OnBecameStable`, which unblocks `/state/wait` and post-action stability waits.

**Triggers:**
- `CombatManager.SetUpCombat` (patch) — subscribes to combat events
- `CombatManager.TurnStarted`, `PlayerActionsDisabledChanged` — combat state changes
- `CombatManager.CombatEnded` — unsubscribes from combat
- `NOverlayStack.Push` / `Remove` (patches) — overlay screen changes
- `EventModel.SetEventState` (patch) — event option changes
- `RunManager.RoomEntered` (event) — room transitions
- `NPlayerHand.BeginSelectCards` (patch) — hand selection prompts
- `NMapScreen` opened (patch) — map transitions
- `NMainMenu` ready (patch) — main menu detection
- `NCharacterSelectScreen` opened (patch) — character select detection
- `ActionExecutor.BeforeActionExecuted` / `AfterActionExecuted` — action queue events

**Stable conditions:**
- **Combat:** play phase, player actions not disabled, no action currently running
- **Overlay present:** any overlay screen (rewards, card selection, game over) counts as stable
- **Hand selection:** hand in card selection mode
- **Interactive room:** map, rest site, shop, treasure
- **Event room:** event has options available or is finished
- **Pre-run screens:** main menu, character select

**Flow:**
1. A trigger fires → `ScheduleStabilityCheck()` queues a deferred check
2. `CheckStability()` runs on the main thread, evaluates `IsStable()`
3. If stable and wasn't stable before → fires `OnBecameStable`
4. `HttpServer.SignalDecisionPoint()` unblocks waiting requests

## Threading Model

The HTTP server runs on a background thread. Game state must be read and actions must be executed on Godot's main thread.

- **State reads** (`GameStateSerializer.Serialize`) access game objects directly. These are called from the HTTP thread but read stable snapshots.
- **Actions** are marshalled to the main thread via `GodotMainThread.RunAsync()` / `ClickAsync()` which use `Callable.From(...).CallDeferred()`:
  - Card plays: `card.TryManualPlay(target)` — same path as the game's UI drag-and-drop
  - End turn: `ActionQueueSynchronizer.RequestEnqueue(EndPlayerTurnAction)` — same as the end turn button
  - Button clicks: `ForceClick()` via `ClickAsync` — for map nodes, events, rewards, rest, shop
  - Card selection: `holder.EmitSignal(Pressed, holder)` — same as AutoSlay's CardRewardScreenHandler
  - Hand selection: `holder.EmitSignal(Pressed, holder)` + confirm button click
  - Potion use: `potion.EnqueueManualUse(target)`
  - Shop purchase: `entry.OnTryPurchaseWrapper(inventory)`
- After executing an action, the HTTP handler waits up to 10s for `OnBecameStable` before returning the response.

## Event Logging

Gameplay events are captured via HarmonyX patches on game hooks and commands. Events accumulate in a thread-safe queue (`EventLog`) and are drained into the `events` array on each state serialization. This gives the agent a record of what happened between decision points.

Events are cleared when entering pre-run screens (main menu, character select) to prevent stale events from leaking across runs.

## Logging

All activity is logged to `~/sts2agent.log` (UTF-8 with BOM). The stability detector logs full serialized state at every decision point. Action requests and results are logged by the HTTP server. Log level can be changed at runtime via the `/log-level` endpoint.

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
| Shop | `MegaCrit.Sts2.Core.Entities.Merchant/MerchantInventory.cs`, `MerchantEntry.cs` |
| Rest site | `MegaCrit.Sts2.Core.Rooms/RestSiteRoom.cs`, `RestSiteOption.cs` |
| Character select | `MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect/NCharacterSelectScreen.cs` |
| Main menu | `MegaCrit.Sts2.Core.Nodes.Screens.MainMenu/NMainMenu.cs` |
| Game over | `MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen/NGameOverScreen.cs` |

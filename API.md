# STS2 Agent API Reference

## HTTP Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/state` | Returns current game state snapshot |
| `GET` | `/state/wait?timeout=<ms>` | Blocks until a decision point is reached (default 30s, max 120s). Returns `{"timeout": true}` on timeout |
| `POST` | `/action` | Execute an action. Body: JSON with `type` field + action-specific params |
| `GET` | `/map` | Full map serialization |
| `GET` | `/log-level?level=<debug\|info\|error>` | Get or set log level |

Base URL: `http://localhost:57541`

## Typical Flow

```
loop:
  GET /state/wait          → get state at decision point
  POST /action { ... }     → execute chosen action (returns updated state)
```

## Action Response Format

**Success:**
```json
{
  "status": "ok",
  "message": "...",
  "stable": true,
  "state": { /* full game state */ }
}
```

**Error:**
```json
{
  "error": "description"
}
```

---

## State Envelope

Every `/state` or `/state/wait` response has this top-level structure:

```json
{
  "context": "<context_string>",
  "player": { ... },
  "available_commands": [ ... ],
  "overlay": { ... },
  "events": [ ... ]
}
```

- `context` — The room type string (see table below). For overlays, this is the *underlying* room type, not the overlay itself.
- `player` — Present for all in-run contexts. See [Player Object](#player-object).
- `available_commands` — Array of command objects valid for the current state. Each has a `type` field matching an action type.
- `overlay` — Present only when an overlay (card_selection, hand_select, rewards) is active.
- `events` — Present only when accumulated game events exist. Each: `{type, message, details?}`.

### Context Strings

| String | Context |
|--------|---------|
| `main_menu` | Main menu (pre-run) |
| `character_select` | Character selection (pre-run) |
| `map` | Map screen |
| `combat` | Active combat |
| `event` | Event room |
| `rest` | Rest site |
| `shop` | Merchant room |
| `treasure` | Treasure room |
| `game_over` | Run ended |

### Context Resolution Priority

When multiple UI elements are active, the context is resolved in this order:

1. **Map** (if map screen is open)
2. **Game Over** overlay
3. **Card Selection** overlay (card reward, grid selection)
4. **Rewards** overlay
5. **Hand Selection** (discard/exhaust prompts during combat)
6. **Combat** (active play phase)
7. **Room type** (Event, Rest, Shop, Treasure)
8. **Pre-run** (Character Select, Main Menu)

---

## Player Object

Present for all in-run contexts.

```json
{
  "hp": 65,
  "maxHp": 80,
  "gold": 120,
  "relics": [
    { "name": "Burning Blood", "description": "At the end of combat, heal 6 HP." }
  ],
  "potions": [
    { "slot": 0, "name": "Fire Potion", "description": "Deal 20 damage." }
  ],
  "deck": [
    { "name": "Strike", "description": "Deal 6 damage.", "cost": 1 }
  ]
}
```

---

## Contexts & Commands

### 1. Main Menu

**Context:** `main_menu`
**State key:** `main_menu`

**State:**
```json
{
  "screen": "main_menu",
  "has_saved_run": false
}
```

**Commands:**

| Action `type` | Parameters | When available |
|---------------|------------|----------------|
| `start_run` | — | No saved run exists |
| `continue_run` | — | Saved run exists |
| `abandon_run` | — | Saved run exists |

---

### 2. Character Select

**Context:** `character_select`
**State key:** `character_select`

**State:**
```json
{
  "characters": [
    { "index": 0, "name": "ironclad", "locked": false }
  ],
  "selected": "ironclad"
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `select_character` | `index` (int) | Highlight a character |
| `embark` | — | Start the run (character must be selected first) |

---

### 3. Map

**Context:** `map`
**State key:** `map`

**State:**
```json
{
  "act": 1,
  "currentCoord": { "row": 2, "col": 3 },
  "availableNodes": [
    { "coord": { "row": 3, "col": 2 }, "type": "Monster" }
  ]
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `select_map_node` | `index` (int) | Select a node from `availableNodes` by index |

Command objects also include `nodeType` and `coord` for display.

---

### 4. Combat

**Context:** `combat`
**State key:** `combat`

**State:**
```json
{
  "round": 1,
  "currentSide": "Player",
  "energy": 3,
  "stars": 0,
  "hand": [
    {
      "index": 0,
      "name": "Strike",
      "description": "Deal 6 damage.",
      "targetType": "AnyEnemy",
      "cost": 1,
      "playable": true
    }
  ],
  "drawPileCount": 5,
  "discardPileCount": 0,
  "exhaustPileCount": 0,
  "orbSlots": 3,
  "orbs": [
    { "index": 0, "name": "Lightning", "passiveValue": 3, "evokeValue": 8 },
    { "index": 1, "name": "Frost", "passiveValue": 2, "evokeValue": 5 }
  ],
  "playerBlock": 0,
  "playerPowers": [
    { "name": "Strength", "amount": 2, "description": "..." }
  ],
  "enemies": [
    {
      "index": 0,
      "hp": 40,
      "maxHp": 48,
      "block": 0,
      "name": "Jaw Worm",
      "powers": [],
      "intents": [
        { "type": "Attack", "damage": 11 },
        { "type": "Attack", "damage": 5, "hits": 3 }
      ]
    }
  ]
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `play_card` | `cardIndex` (int), `targetIndex` (int, optional) | Play a card. `targetIndex` required when `targetType` is `AnyEnemy` or `AnyAlly` (indexes into `enemies` array) |
| `end_turn` | — | End the player's turn |
| `use_potion` | `slot` (int), `targetIndex` (int, optional) | Use a potion from inventory slot |

**Notes:**
- Commands only available during player play phase
- `targetIndex` references the alive-enemies array (same as serialized `enemies`)
- Card `cost` can be an integer or `"X"`
- `orbSlots` and `orbs` only present for Defect (when orb capacity > 0). `passiveValue`/`evokeValue` reflect current values after modifiers

---

### 5. Event

**Context:** `event`
**State key:** `event`

**State:**
```json
{
  "title": "Big Fish",
  "description": "You see a big fish...",
  "options": [
    { "index": 0, "label": "Eat", "description": "Heal 5 HP.", "locked": false },
    { "index": 1, "label": "Feed", "description": "Obtain a relic.", "locked": false }
  ]
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `select_event_option` | `optionIndex` (int) | Select an unlocked event option |
| `proceed` | — | Available when event is finished |

---

### 6. Rest Site

**Context:** `rest`
**State key:** `rest`

Also includes `map` key with available next nodes.

**State:**
```json
{
  "options": [
    { "index": 0, "id": "rest", "name": "Rest", "description": "Heal 30% of max HP.", "enabled": true },
    { "index": 1, "id": "smith", "name": "Smith", "description": "Upgrade a card.", "enabled": true }
  ]
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `rest_option` | `option` (string) | Select by option ID (e.g. `"rest"`, `"smith"`) |
| `proceed` | — | Available after resting (when proceed button is enabled) |

---

### 7. Shop

**Context:** `shop`
**State key:** `shop`

**State:**
```json
{
  "isOpen": true,
  "gold": 200,
  "items": [
    { "index": 0, "type": "card", "name": "Uppercut", "description": "...", "cost": 75, "affordable": true },
    { "index": 1, "type": "relic", "name": "Vajra", "description": "...", "cost": 150, "affordable": true },
    { "index": 2, "type": "potion", "name": "Fire Potion", "description": "...", "cost": 50, "affordable": true },
    { "index": 3, "type": "card_removal", "name": "Remove Card", "cost": 75, "affordable": true }
  ]
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `shop_open` | — | Open the merchant's inventory (when `isOpen` is false) |
| `shop_buy` | `itemIndex` (int) | Purchase an item (must be `affordable`) |
| `shop_leave` | — | Leave the shop (always available) |

---

### 8. Treasure

**Context:** `treasure`
**State key:** `treasure`

**State:**
```json
{
  "relics": [
    { "index": 0, "name": "Golden Idol", "description": "..." }
  ],
  "proceedAvailable": true
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `proceed` | — | Continue after claiming relics |

---

### 9. Game Over

**Context:** `game_over`
**State key:** `game_over`

**State:**
```json
{
  "victory": false,
  "seed": "ABC123",
  "ascension": 0,
  "run_time": 1234.5,
  "floor_reached": 3,
  "killed_by": "Jaw Worm",
  "score": 150,
  "character": "铁甲战士",
  "deck_size": 12,
  "relic_count": 3
}
```

| Field | Type | Description |
|-------|------|-------------|
| `victory` | bool | Whether the run was won |
| `seed` | string | Run seed |
| `ascension` | int | Ascension level |
| `run_time` | float | Run duration in seconds |
| `floor_reached` | int | Number of acts visited |
| `killed_by` | string? | Localized name of encounter/event that killed the player (defeat only) |
| `score` | int | Calculated run score |
| `character` | string | Localized character name |
| `deck_size` | int | Final deck size |
| `relic_count` | int | Final relic count |

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `continue` | — | Advance the game over screen. First call clicks Continue (plays summary animation), second call clicks Main Menu |

---

## Overlays

Overlays appear on top of a room context. When an overlay is active:
- `context` = the underlying room type (e.g. `"combat"`)
- `overlay` = the overlay-specific state (with a `type` field)
- `available_commands` = commands for the overlay (not the underlying room)
- The underlying room state is also included (e.g. `combat` key with full combat state)

### 10. Card Selection (overlay)

**Overlay type:** `card_selection`

Appears for: card rewards after combat, card removal, card transform, shop card removal.

**Overlay state:**
```json
{
  "type": "card_selection",
  "cards": [
    { "index": 0, "name": "Uppercut", "description": "...", "cost": 2 }
  ],
  "canSkip": true,
  "minSelect": 1,
  "maxSelect": 1
}
```

`minSelect`/`maxSelect` only present for multi-pick screens.

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `select_card` | `cardIndex` (int) | Select a card by index |
| `skip` | — | Skip selection (only when `canSkip` is true) |

---

### 11. Hand Selection (overlay)

**Overlay type:** `hand_select`

Appears during combat for discard/exhaust prompts (e.g. "choose a card to exhaust").

**Overlay state:**
```json
{
  "type": "hand_select",
  "prompt": "Choose a card to discard.",
  "minSelect": 1,
  "maxSelect": 1,
  "selectedCount": 0,
  "cards": [
    { "index": 0, "name": "Strike", "description": "Deal 6 damage." }
  ]
}
```

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `choose_hand_cards` | `cardIndex` (int) | Toggle selection of a card |
| `confirm_selection` | — | Confirm selection (only when min selection met) |

---

### 12. Rewards (overlay)

**Overlay type:** `rewards`

Appears after combat victory or other reward events.

**Overlay state:**
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

Reward types: `gold`, `card`, `potion`, `relic`, `card_removal`.

**Commands:**

| Action `type` | Parameters | Description |
|---------------|------------|-------------|
| `select_reward` | `rewardIndex` (int) | Claim a reward (selecting `card` opens a card_selection overlay) |
| `proceed` | — | Continue (only when `canProceed` is true) |

# sts2agent

A Slay the Spire 2 mod that exposes the game state and actions over a local HTTP API, enabling programmatic control of the game.

## Features

- **Full game state serialization** — player stats, combat details, map, events, shop, rewards, and more
- **Action execution** — play cards, select map nodes, pick event options, buy items, and navigate every screen
- **Decision point signaling** — long-poll endpoint that blocks until the game reaches a point requiring input
- **Event logging** — tracks game events (damage, card plays, relics gained, etc.)

## Installation

1. Download `sts2agent.dll` and `sts2agent.pck` from the [latest release](https://github.com/wdong/sts2agent/releases)
2. Copy both files to your STS2 mods folder:
   ```
   <Steam>/steamapps/common/Slay the Spire 2/mods/
   ```
3. Launch the game — the mod loads automatically

## API

The mod starts an HTTP server on `http://localhost:57541`.

### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/state` | Current game state snapshot |
| `GET` | `/state/wait?timeout=<ms>` | Block until a decision point (default 30s, max 120s) |
| `POST` | `/action` | Execute an action (JSON body with `type` + params) |
| `GET` | `/map` | Full map data |
| `GET` | `/log-level?level=<debug\|info\|error>` | Get or set log level |

### Typical loop

```
GET  /state/wait        → receive state at decision point
POST /action { ... }    → execute chosen action, receive updated state
```

See [API.md](API.md) for the full reference including all contexts, state schemas, and available commands.

## Supported Contexts

The mod handles every game screen:

- **Main Menu** / **Character Select** — start, continue, or abandon runs
- **Map** — navigate between nodes
- **Combat** — play cards, use potions, end turn
- **Events** — select options
- **Rest Sites** — rest, smith, or other campfire options
- **Shop** — browse and buy cards, relics, potions, and card removal
- **Treasure** — claim relics
- **Rewards** — collect gold, cards, potions, relics
- **Card/Hand Selection** — pick cards from reward screens or discard/exhaust prompts
- **Game Over** — advance through the summary screen

## Building from source

Requires .NET 9.0 and a local STS2 installation (for `sts2.dll` reference).

```bash
dotnet build
```

Copy the output DLL to the STS2 mods folder.

## Example Agent

See [ClaudePlaysSTS2](https://github.com/ndwang/ClaudePlaysSTS2) for an example agent that uses this mod's API to play the game.

## Logs

The mod writes to `~/sts2agent.log`. Set the log level at runtime via the `/log-level` endpoint.

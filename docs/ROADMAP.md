# Ember Multiplayer: roadmap

## Status (2026-10-20, night)

| Phase | State |
|---|---|
| A Steam + menus | **Done**: transport layer (LiteNetLib LAN/IP, Steam Networking Sockets P2P + IP), Steam lobbies (friends / invite / public), rich presence "Join Game", overlay + in-game invites, launch from an invite, public game browser, readable refusals (version, full), main-menu MULTIPLAYER button + badge + version, host/join screens, pause-menu multiplayer (players, invite, kick, leave). A8 voice chat (Steam push-to-talk V / open mic, played through Windows since the game has Unity audio off; tested with a synthetic tone). Open: A7 a real two-account Steam test (needs you + a friend; also the first real microphone test). |
| B Shared-world foundation | **Done**: join into the host's world (from the menu or mid-game), guest saves write the character only, independent travel with per-scene runners + handoff (3 players tested), story flags, lore/recipes/blueprints, Tinetia unlocks, dialogue variables, time of day, quests (merged), kills in other scenes count, a scene's deaths for players arriving later and a fight that carries on (effects, the Protector's aura, engaged groups) when its runner leaves or drops (`deadstay`, `fxtakeover`), no pausing with others around, co-op deaths, quest rewards for every player in the world (once per character), story cutscenes for everyone in the room, story scripts run by one player (dialogue events, trigger zones, timed triggers, lore pages) changing the world for everyone, flags reaching the loaded scene live (grow-only), rewards outside quests for every player once, Tinetia's map unlocks bought once (host decides), the spider nest door and Tinetia's first talk played per room, scripts setting the clock forwarded to the host. |
| C World objects | **Done for everything in the demo**: doors; levers/switches/pressure plates/item locks/torches; room events and enemy waves (runner decides); totem puzzles (the runner's sequence for everyone, hits counted by the runner); traps on the host clock; breakables (drop once); secret rooms; chests, world drops and pickups with a host **loot mode** (each player their own / shared, first to grab); storage chests (the world's stash: one, the host's, in both modes; guests' crafting/repair consumption from shared chests goes to it) and key items (the world's: what a guest gets or uses changes the host's world); gathering nodes; shops (runner-owned stock, purchases as intents); saved story objects; crafting stations (runner-owned queues, collector gets items); repairs; NPC "busy talking" lock. Known gaps: breakables spawned inside room events, a wave stalls if its runner leaves mid-wave, crafting queue race (guest ingredients lost). (A shared chest's holder now hands over each take as it happens: dropping out duplicates nothing.) |
| D Hub/estate | **Synced, waiting for a real estate (Early Access)**: the demo has no estate scene (`CreativeArea` isn't in the build) nor any estate object or database (survey: `estatescan`). Building, tiles and farming are synced against the game's own classes and tested on a built-in test estate (`estate`, `estate-late`): changes to the one estate (placeables, tiles, soil states) go to everyone, live or into the estate data; harvests through the estate scene's runner; the host's save keeps it. Host options: estate unlocked (story flags; makes the town's repairs work) and keep playing past Gumu's demo end (`demoend`). Crafting stations and repairs are done (C). World map (D5) shared: fog, region portals, markers, quick portals (host-given ids). Not yet: placed crafting stations and placed trees/gathering (runtime objects), portals in a real scene. |
| E Polish | **Mostly done**: release zips, lag-tested (~300 ms RTT, 5% loss), 4 players (`crowd`), reconnect, voice, host settings (loot mode, teleport, estate, keep playing), party HUD (health, Kymia, arrows, minimap and world map), pings and teleport to teammates, death announcements (cause, banner, boss kills), other players' spells/skills and status effects on screen, fishing, save backups before every session, hardened networking (per-message checks and rate limits, fuzz-tested). Patches apply class by class; every test checks all hooks took. |
| P Early Access port | From 2026-10-27. |
| M Mod API | **Done, to be tested by the lead**: a public API for other mods (`EmbervilleMP.Api.Multiplayer`, [API.md](API.md)): players, who decides, events, channels through the host, world/session stores, object kinds, spawned objects, `[MultiplayerMod]` compatibility at join (protocol 20). Example mods `mods/PartyTricks` (cheers, campfires) and `mods/DamageColors`, released separately. Scenarios `api-*`, `partytricks-*`. Open: the lead's first runs; `store.py --mod` for publishing the example mods; Steam lobby data could show a host's required mods before joining. |

Tests: `scripts\test-all.ps1` (headless, chained; `-Quick` for the everyday check), `-Lag "60,3"` for internet conditions.

Sources: two code surveys of the decompiled game (world objects/interactables, progression/story/session), the
Steam spike (`--mp-test=steamcheck`), the main menu capture (`--mp-test=menu`), scene surveys (`survey`, `estatesurvey`).

> **Early Access lands on 2026-10-27** (one week). The demo's progress carries over, so the full game is a
> different build and a different Steam app. Phase P covers the port; until then everything is built against
> the demo, and nothing should hard-code demo-only facts (app id, scene names, BoolID values).

## Recommended order

1. **Phase A: Steam + multiplayer menu** (first: it changes how people join, and real internet play will
   show latency problems in the combat sync before more is built on top).
2. **Phase B: shared-world foundation** (join into the host's world, safe saving, scene travel, time, story
   flags, quests). Everything in C/D depends on it.
3. **Phase C: world objects** (doors, chests, drops, gathering, switches, shops...).
4. **Phase D: the hub/estate** (farming, building, crafting, repairs, portals, map).
5. **Phase E: polish**.
6. **Phase P: Early Access port**, as soon as it's out (slots in wherever we are).

Sizes: S = a session, M = a few sessions, L = a big chunk. Each item ends with a self-test scenario.

---

## Phase A: Steam + multiplayer menu

Viability is proven: the mod starts Steam's API inside the game as the Emberville Demo app (5355880), logs in,
reaches the Steam relay network (`k_ESteamNetworkingAvailability_Current`) and creates a friends-only lobby
with data. The game itself has no Steam code, so the mod ships Steamworks.NET 2025.164.1 + `steam_api64.dll`.

| # | Item | Size | Notes |
|---|---|---|---|
| A1 | **Transport layer** under `Session`: LiteNetLib (LAN/IP) and Steam Networking Sockets (P2P over Valve's relay: no port forwarding, works through NAT). All sync code keeps using `Session.Send/On`. | M | Steam sockets can also connect by IP, so the Steam code path runs in the two-copies-on-one-PC tests (one Steam account can't P2P to itself). |
| A2 | **Steam lobbies**: host creates one (friends-only / invite-only / public, max players), sets rich presence so friends see "Join Game", overlay invites (`GameLobbyJoinRequested`), launching the game from an invite (`+connect_lobby`), lobby data (mod/protocol version, host name, scene). | M | `SteamAPI.RunCallbacks` every frame (the game never calls it). App id from Steam at runtime, never hard-coded (EA will be a different app). |
| A3 | **Version handshake**: protocol + mod version + game build; a clear "update the mod / the game" refusal. | S | Partly there (connect key). |
| A4 | **Main menu**: a native-looking **MULTIPLAYER** button between PLAY and SETTINGS (cloned from the game's button so it matches); a "MULTIPLAYER" badge on the Emberville logo; mod version under the game version (top right). | M | Menu: centre logo, PLAY / SETTINGS / EXIT, patch notes left, social icons right. The welcome panel shows every launch. |
| A5 | **Multiplayer screen**: Host (Steam lobby or LAN), Join (friends' games, public games, LAN, by IP), choose which character/world you bring. Replaces F8 as the main way in (F8 stays in game). | M | Ties into B1: a guest joins *into the host's world*. |
| A6 | In-game player list (name, ping, room), invite friends, leave, host kicks. | S | |
| A7 | **Real Steam test with two accounts** (you + a friend or a second account/PC). | S | The only part I can't self-test. |
| A8 | (Optional) Steam voice chat, push-to-talk. | M | Coal LLC has it; nice for LAN-less play. |

## Phase B: shared-world foundation ("host's world, guests' characters")

The game keeps one character file (`.evp`) and three world files (`.evw` world, `.evm` map, `.eve` estate).
It saves everything on **every scene change and on quit**, and every scene re-runs all load handlers from memory.

| # | Item | Size | Notes |
|---|---|---|---|
| B1 | **Join into the host's world**: the guest loads its own character, receives the host's World/Map/Estate snapshot in memory (taken after the host's save listeners run), uses a throw-away world prefix, and loads the host's scene next to the host. | L | The core of shared world. |
| B2 | **Guest saving is character-only**: `SaveData` on a guest writes `.evp` only; world/estate/map writes and deletes are blocked; the guest's own worlds are never touched. Character-like data the game stores in the *world* (tutorials done, checkpoint) gets a guest side file. Key items are the world's, not the character's: shared under the host's authority (`Sync/KeyItems.cs`, `keyitems`). | M | Must be bullet-proof: progress carries over to EA. Tutorials done: `<character>_mp_tutorials.txt` (`Sync/Tutorials`). Items changing hands (stash, key items, a guest's purchases and loot) save both sides within seconds, debounced, at safe moments (`Sync/SaveSync.cs`, `savesync`); side files `<save>_mp_*` go when their save is deleted and name their world (`Sync/SideFiles.cs`, `sidefiles`). |
| B3 | **Scene travel is host-led**: a guest at a scene exit asks; the host moves the group (`SceneChange(scene, teleporter, seed)`). Within a scene, free roaming as now. Includes the Crystal cutscene's forced scene change. | M | Rooms already work split up (stage 2). |
| B4 | **Time of day**: host's `GameDateTime` ~1/s + on `SetDayTime`; guests adopt it (lights, clock, day events). | S | **Done**: plus scripts on a guest setting the time go to the host (`worldclock`), corrections past midnight run the game's new day, the host's clock runs while it's paused with others in the session. |
| B5 | **Story flags**: `gameBools` (one hook: its `OnValueChanged`), lore read, recipes/blueprints known, Tinetia unlocks, dialogue variables, world custom events. Host-owned; guests send intents. | M | **Done**: flags grow only (a false is neither sent nor merged); a flag from another player re-runs the `CheckGameBool`s of the loaded scene that depend on it (`liveflags`); what a player's own scripts do to synced objects is sent (`scripttriggers`); character rewards outside quests go to everyone once (`charrewards`); Tinetia's unlocks are bought through the host (`cartographer`); a merged quest re-runs the loaded scene's `ObjectiveChecker`s / `QuestChecker`s (`livequests`); teleporters' and rooms' one-way SetActive calls are shared (the trap room's secret teleporter: `teleportreveal`); recipes others learn show the game's notification (`learnednotice`); only saved dialogue bools are shared (runtime ones are the conversation's, game bools are flags). `DefeatedSpiderMatriarch` and `MetRuton` share id 24 in the game. |
| B6 | **Quests**: merged quest states (grow-only; a guest only once in the host's world); kill objectives count each kill once for the group; collect objectives: first player holding the items completes it for the group; on completion every player in the session gets the character rewards once (per character and world, `_mp_quest_rewards.txt`), late joiners don't (except the world's one-time class lessons: the Mind's Journal teaches the Wanderer only while the humming-sphere objective is open, so a later character gets it once that objective is done, `Sync/ClassLessons.cs`, `classlessons`), dropped-and-rejoined players catch up. | M | **Done** (`questrewards`). Open: a counted objective fed by each player's own actions (ObjectiveIncrementer, QuestEventsHandler) merges as the highest count, not the sum; `worldsurvey` marks such objectives (count > 1) per scene. |
| B7 | **Cutscenes**: host triggers, everyone plays it locally (camera, letterbox, NPC moves); arena locks (Matriarch, Adeos) pull late players in instead of shutting them out. | M | Today each player gets their own intro. |
| B6 | **Quests**: host-authoritative `QuestManager` (state, steps, objectives); kill objectives count once (host); collect objectives per player; on completion each player gets the XP/item rewards on their own character. | M | Snapshot on join + deltas. |
| B7 | **Cutscenes**: host triggers, everyone plays it locally (camera, letterbox, NPC moves); arena locks (Matriarch, Adeos) pull late players in instead of shutting them out. | M | **Done for the tutorial's room cutscenes** (`CutsceneSync`: intro, Adeos, humming sphere gate, Tinetia release/guide/portal, Tinetia's first talk; the spider nest door: `SpiderDoorSync`, `spiderdoor`): whoever sets one off, everyone in that room plays it once; others get the end state; a copy stuck after the others finished is released. Arrival cutscenes (crystal, cartographer, Gumu) stay per player, gated by shared flags; boss intros stay per player. A player who dies in a boss fight is put back into its arena (`bosswipe`); a player in a boss's room whose own intro hasn't played joins the fight another player's intro started (`BossArena.Join`: in the arena, door shut). |
| B8 | **No pausing in co-op, everywhere**: the remaining `Time.timeScale = 0` spots (farming/build selectors, stats prompt, tutorial windows, skill navigation). Tutorials stay per player. | S | Death screen and pause already done. Tutorial windows that stop time wait until the player is out of combat while others are in the scene. |
| B10 | **Deaths in co-op**: only the dead player respawns; room events, enemy waves and graph events fail only once everyone in their room is down (`roomwipe`); a party wipe (everyone in the scene down) rewinds the scene the game's way on every copy, the Matriarch and her eggs included (`bosswipe`); her roar and shakes reach everyone in her arena (`bossroar`). | S | **Done**. |
| B9 | Kill credit/XP: each copy runs the deaths of its scene, so every player there gets each kill's XP once (also from another room of the scene); kill objectives only count real deaths (the game also fires its death event when a room switches off). Not host-owned numbers: every copy counts every kill once and merges take the highest. | S | **Done** (`killcredit`). |

## Phase C: world objects

Common plumbing first: every interaction goes through `InteractablesHandler.OnInteract`, so one patch turns
"I pressed E on X" into an intent to the host; the host runs the game's own code and broadcasts the result.
Object identity = the game's own GUID/ID where it has one, else the scene path (as for enemies).

| # | Item | Size | Notes |
|---|---|---|---|
| C1 | **Interaction plumbing** (intent → host → state broadcast; snapshot on join; per-object locks). | M | |
| C2 | **Doors, gates, locks, keys** (`GateDoor`: open/close/unlock, scripted states, auto-close). Keys are world items already. | S | The game's `SaveDoorState` never updates an entry (only the first state sticks): keep our own state. |
| C3 | **Switches, levers, puzzles, room events, traps, destructible walls/pots, secret rooms** (`InteractableSwitch/Group`, `DungeonRoomEvent` stages, `GroundTrap`/`ShooterTrap`, `DamageableObject`, `RoomHider`). | M | Traps on a shared clock. |
| C4 | **Chests**: host rolls contents (each copy rolls differently today; chests without an id even get a random one), one shared chest, locked while someone has it open (or per-move ops). | M | **Done**: loot chests follow the loot mode (`lootchest`, `lootchest-shared`); storage chests are one host-owned stash by id in both modes, moves as per-move ops (`storagechest`). |
| C5 | **World item drops and pickups**: host spawns them (incl. random stats), whoever picks one up gets it, it disappears for everyone. Breakables/bushes drop through this. | M | Today enemy loot is per player (instanced). Items a player throws away from its inventory are network drops in both loot modes, first to grab (`throwdrop`). |
| C6 | **Gathering nodes** (trees, rocks, herbs): host-owned health/state, drops via C5; gather animation and job XP local. | S | No respawn timers exist in the game. |
| C7 | **Shops**: host-owned stock; buying/selling validated by the host; gold stays per player. | S | |
| C8 | **Generic saved story objects** (`GameObjectStateLoader`, NPC positions saved by GUID). | S | |
| C9 | NPCs: positions/activity via the enemy sync (they are Enemy-based), optional "busy talking" lock. | S | **Done**: busy lock; while someone talks, every copy turns the NPC to them and shows the talking bubble, the runner's states don't turn it back (`npclook`). |

## Phase D: the hub (estate) and the world map

| # | Item | Size | Notes |
|---|---|---|---|
| D1 | **Farming**: plow/plant/water/fertilize/harvest as intents; host runs growth on its clock; soil state broadcast. | M | Needs B4. |
| D2 | **Building and tilesets**: place/remove via the host (it makes the save ids), costs from the builder or a shared chest. | M | |
| D3 | **Crafting stations**: host-owned queues and timers; collect via the host. | S | |
| D4 | **Repairables** and destroyed portals. | S | |
| D5 | **World map**: portal states, quick portals (their ids collide across machines today), markers, fog (union of everyone's explored cells). | M | `Sync/MapSync.cs`, tests `mapshare`, `mapshare-late`. The demo never builds its map in play (no `DungeonScene`): the tests drive the map data directly. |
| D6 | **Procedural dungeons** (`DungeonScene`): same seed for everyone, seeded or host-sent chunk choices, host-placed enemies/events. Not in the demo build; needed for EA. | L | Chunks and barriers roll their props with `RandomAsset.Change` (`Random.value`, per copy): seed it too. |

## Phase E: polish

- Main menu art pass (badge/banner art, screenshots for the README); in-game HUD (who's where, ping).
- Host settings: who can join, loot mode, friendly fire off/on. (Friendly fire is off: players' hits, arrows and
  bombs never hurt teammates, `friendlyfire`; replayed enemy attacks aim, stop and die like the runner's,
  `enemyreplay`. An "on" option is not done.)
- Reconnect after a drop; host leaving ends or hands over the session (host migration like Coal LLC is big: maybe later).
- 3-4 player tests (the code allows 4; tests run 2).
- Bandwidth/latency pass for internet play (snapshot rates, interpolation delay, enemy hit timing over 100+ ms).
- Installer (one-click: copies BepInEx + mod, checks the game version) and a player-facing README.
- Teammates touching the world: **done** for grass sway, critters running off, ghosts vanishing (`PuppetTouch`),
  training dummy hits and recycler runs (`WorldMoments`), placed objects' random looks (enemy colours) picked by scene
  path, teammates' heal/buff areas on our character (`throwdrop`, `teamheal`, `worldtouch`, `worldmoments`). Not done:
  critters walk their own paths on each copy (`FaunaPath` rolls on each copy's `Time.time` and `Random`: a seed alone
  wouldn't line them up; needs the runner's moves sent); ghosts aren't in the demo (a vanish by a teammate plays on
  each copy that sees it; `onVanishEvent` isn't hooked to any state we sync).

## Phase P: Early Access port (from 2026-10-27)

- Checklist: [EA-PORT.md](EA-PORT.md). First step: `.\scripts\ea-check.ps1 -GameDir <EA build>` lists every hook, call,
  lookup, enum value and scene of ours that is missing or changed, by impact and feature.
- Install BepInEx into the EA build, re-decompile `Assembly-CSharp`, diff against the demo's, fix changed hooks.
- Steam app id comes from Steam (no change needed if A2 follows the rule above).
- Re-run `test-all.ps1`; re-survey scenes (new areas, procedural dungeons → D6).
- Decide whether to keep supporting the demo (probably not once EA is out).

## Decisions to make (defaults in **bold**)

1. **Loot**: enemy drops are per player today (**keep instanced**). Chests and world drops: shared ("first to grab
   gets it") or **instanced per player** (everyone gets their own roll; easiest and friendliest).
2. **Scene travel**: **the group follows the host between scenes**; or everyone travels independently (the host must
   then run scenes it isn't in: much harder).
3. **Guest story progress**: **guests progress the host's world only** (their own world stays where it was), like
   Terraria/Valheim. Their character (XP, items, classes) always comes home with them.
4. **Keys and quest items found by a guest**: **go to the host's world** (shared progress).
5. **Who can join**: **friends only** by default; invite-only and public as options.

# Developing Ember Multiplayer

Everything needed to build, test and extend the mod. For what the mod does, see the [README](../README.md); for
what's next, [ROADMAP.md](ROADMAP.md).

- [Building](#building)
- [Project layout](#project-layout)
- [How it works](#how-it-works)
- [Testing](#testing)
- [Parallel work](#parallel-work)
- [Gotchas](#gotchas)
- [Releases](#releases)

## Building

Requirements: Windows, the .NET SDK, Emberville Demo installed (the build references the game's own assemblies).

1. Unzip `downloads\BepInEx_win_x64_5.4.23.5.zip` into the game folder and run the game once (BepInEx sets itself up).
2. `.\scripts\build.ps1` builds the plugin and copies it, LiteNetLib, Steamworks.NET and `steam_api64.dll` into
   `<game>\BepInEx\plugins\EmbervilleMP\`.

Another game folder: `$env:EMB_GAMEDIR = "E:\path\to\Emberville Demo"` (or `-p:GameDir=...` to `dotnet build`).

The plugin targets `netstandard2.1`, references the game's `Assembly-CSharp.dll` through
[BepInEx.AssemblyPublicizer](https://github.com/BepInEx/BepInEx.AssemblyPublicizer) (private members are reachable),
and patches the game with HarmonyX. Each patch class is applied on its own: a game update that breaks one hook only
disables that feature (logged as `[patch]`), and every self-test checks that all hooks took.

The game's decompiled code is a local reference only (`notes/decompiled/`, git-ignored). Regenerate it with
[ILSpy](https://github.com/icsharpcode/ILSpy) (`ilspycmd`) from `Emberville_Data\Managed\Assembly-CSharp.dll`.

## Project layout

| Path | What |
|---|---|
| `src/EmbervilleMP/Net/` | `Session` (star topology: the host relays guests; handshake with readable refusals), transports (`LiteNetTransport` LAN/IP, `SteamTransport` Steam P2P / IP, `LagTransport` for tests), `Protocol` (message ids, version) |
| `src/EmbervilleMP/Steam/` | `SteamBootstrap` (loads `steam_api64.dll`; app id from Steam), `SteamLobby` (lobbies, rich presence, invites, public list), `Voice` + `WaveOut` (voice chat) |
| `src/EmbervilleMP/Sync/` | players (`PlayerState`, `PlayerLook`, `RemotePlayer`, `PuppetBuilder`, `Presence`, `Chat`, `PlayerFx`, `TeamSounds`: their sounds, `TeamFx`: floating text, teleport/respawn flashes, death marks, attack speed; `FishingSync`; `PuppetTouch`: grass, critters and ghosts react to puppets; `WorldMoments`: training dummy hits, recycler runs), combat (`EnemySync`, `EnemyFx`: enemy effects, visuals, runner-resolved hits; `EnemyCombat`: who's in combat, on every copy; `HostWorld`, `RemotePlayerTarget`, `EggSync`, `BossSync` (the Matriarch's stage and web), `BossArena` (any boss cinematic of the game's shape, found by reflection: its events for everyone in its arena, back into the fight after a death, its rewind), `TrackerSync` (hooks, camouflage), `DamageCodec`), `PartyWipe` (what a death fails or rewinds: room events, waves and graph events only with everyone in the room down; a party wipe rewinds the scene on every copy), `SceneAuthority`, `GuestWorld` (the host's world on a guest, save guards), `WorldState` (flags, lists, time, quests), loot (`LootRules`, `WorldDrops`, `ItemCodec`), `KeyItems` (the world's key items, host-owned), the estate (`EstateRules`: host options, demo end; `EstateSync`: building, tiles, farming), `Tutorials` (a guest's own record, windows held back in a fight), `Backups`, `SaveSync` (both sides save after items change hands), `SideFiles` (the mod's files go with their saves), `ClassLessons` (late characters get the world's one-time class lessons), `NetIds`, `FriendlyFire`, `EnemyAim` (replayed attacks aim at the runner's view of us) |
| `src/EmbervilleMP/Sync/Objects/` | `ObjectSync` (world-object framework: states, snapshots, intents, the host's object table) and one handler per kind of object (doors, switches, room events, traps, breakables, secret rooms, chests, gathering, shops, crafting, repairs, saved objects, NPC talk lock, story cutscenes, portals, the spider nest door, script scopes and their SetActive calls); `StashSync` (storage chests: one host-owned stash by chest id, not per scene object) |
| `src/EmbervilleMP/UI/` | `MainMenuMP`, `PauseMenuMP` (built from the game's own buttons), `PartyHud` + `WorldMapParty` (party HUD from the game's own bars, font and map boxes; vitals from `Sync/PartyStatus`), `MPWindow` (F8 developer window), `ChatBox` |
| `src/EmbervilleMP/Patches/` | `EnemyPatches`, `DeathPatches` (co-op death), `SpeedPatches` (splash skip, faster loading) |
| `src/EmbervilleMP/Api/` | the public API for other mods ([API.md](API.md)): `Multiplayer` (the entry point), `MpPlayer`, `MpChannel` + `ApiChannels` (mods' messages over `Msg.ModMessage`, the host checks and relays), `MpStore` + `ApiStores` (shared data, `Msg.ModState`), `MpObjectHandler` (mods' kinds in `ObjectSync`), `ApiSpawns` (spawned objects, `Msg.ModSpawn`), `ApiMods` + `MultiplayerModAttribute` (declared mods in the join handshake), `ApiSideFile` (`<world>_mp_mods.txt`), `ApiGuard` (every call into a mod try/caught, logged under its GUID), `Testing` (`[MpScenario]`, `Multiplayer.Testing`) |
| `src/EmbervilleMP/Dev/` | `DevHarness` (profiles, autostart, screenshots), `SelfTest*.cs` (scenarios), `ApiTestMod` (a test-only mod for the `api-*` scenarios), `Watchdog`, `FileLog` |
| `scripts/` | `build.ps1` (also compiles the mods built on the API from [emberville-mods](https://github.com/HardHeadHackerHead/emberville-mods), cloned next to this repo or at `EMB_MODS`, against this build; `-WithMods`: deploys them to their own plugin folders, for tests), `test-all.ps1`, `run-pair.ps1`, `run-test.ps1`, `package.ps1`, `ea-check.ps1` (game dependency inventory and checker, see [EA-PORT.md](EA-PORT.md)) |
| `tools/EaCheck/` | the checker behind `ea-check.ps1` (Mono.Cecil); `ea-hints.txt` lists members reached dynamically |
| `docs/` | `API.md` (for mod authors), `ROADMAP.md` (plan and status), `EA-PORT.md` / `EA-INVENTORY.md` (moving to Early Access), `images/` (README screenshots) |

## How it works

- **Sessions.** One host, up to three guests. Every packet is `[message][player id][payload]`; guests talk to the host,
  the host relays. Transports are interchangeable: LiteNetLib (LAN / direct IP) or Steam Networking Sockets (P2P over
  Valve's relay, no port forwarding).
- **Other players** are clones of the local character with everything but the visuals stripped, driven by 20 Hz state
  (position, animator states per layer, parameters) through a 100 ms interpolation buffer, and by "look" messages
  (rig parts, colours, gear).
- **Scene authority.** Players travel independently. Each scene is *run* by the host if it's there, else by the lowest
  player id in it. The runner simulates enemies (AI, health, attacks); everyone else shows puppets. When the runner
  leaves, the next player takes over mid-fight with a state handoff.
  A scene's deaths last as long as someone is in it (the game's rule: they reset when the scene loads again): the
  runner tells each player arriving which enemies are dead, and they're put down without a second death
  (`EnemySync.Dead.cs`). A runner that leaves or drops hands nothing over: the player taking over makes the effects it
  mirrored real (burns, shields, the Acolyte Protector's aura and march) and keeps fighting groups engaged
  (`EnemyFx.Takeover.cs`, `EnemyFx.Protector.cs`, `EnemyCombat.cs`). "In combat" (quick portal, teleport to a
  teammate, tutorial windows) means an enemy fighting near you or after you in your room, on every copy.
- **Host's world, guests' characters.** A joining guest receives the host's World/Map/Estate data (gzipped JSON),
  loads it under a throw-away world name, and never writes it to disk; only the guest's character file saves. Story
  flags, lore, recipes, Tinetia unlocks, dialogue variables and quests merge "grow-only" (progress never goes back);
  the host's clock is everyone's.
- **Quests and rewards.** Quest snapshots merge "most advanced wins"; a guest only sends and merges them once it
  plays in the host's world (its own world's quests never leak in). The game grants rewards when a quest's last step
  finishes (`Quest.Reward`), on each copy that completes it, its own play or a merge (`Sync/QuestRewards.cs`):
  character rewards (XP, items, gold, classes) go to **every player in the session when it completes, whatever scene
  they're in, once per character and world** (record: `<character>_mp_quest_rewards.txt`, saved with the character).
  A guest who joins after the completion gets nothing; one whose connection dropped while the quest was on gets it
  when it rejoins. The exception is a one-time class lesson (a dialogue trigger that unlocks a class and completes a
  quest objective: the Mind's Journal's Wanderer): once the world has that objective done, a guest's character
  without the class gets it, once (`Sync/ClassLessons.cs`). World effects (flags, quest states, markers...) run on
  each completing copy; "consume from inventory" only for the player whose own play completed it. Collect objectives
  are the group's: the first player holding the items completes them for everyone (each sees their own count until
  then).
- **Kill credit.** Each copy runs the deaths in its scene (the runner's, replayed by the others): every player in the
  scene gets the kill's XP once, from their own copy (also from another room of that scene), and only for a player's
  kill, as in the game (`EnemyDied` carries the cause: a self-destruct or a burn's tick gives nobody XP). "Kill X"
  objectives count
  each real death once per copy (kills elsewhere through `EnemyDied`) and merge "highest wins", so the group's count is
  the number of kills; rooms switching off (which fires the game's death event for living enemies) count nothing.
- **Hits between players.** None (`Sync/FriendlyFire.cs`): a hit by a player (or a copy of their effect) on a
  teammate's puppet is dropped on every copy, and whatever ignores the "Player" tag ignores puppets too (a player's
  arrows fly through, the Matriarch's body only hurts on contact while she dashes). Only enemies' (and non-trap
  hazards') hits on a puppet go to that player.
- **Replayed enemy attacks.** The runner sends each attack's start (`EnemyAttackStart`), its interruption by a
  stagger, stun, hit-freeze or death (`EnemyAttackCancel`), and, for an attack on another player, which moment of that
  player its puppet showed (`EnemyAttackAim`): that player's replay aims at a stand-in walking its own recent path from
  that moment (`Sync/EnemyAim.cs`), so the projectile it sees is the one that hits. A replayed death pops no number
  of its own (the killing blow's shows) and uses the runner's gore roll; a corpse follows the runner's for 1.5 s
  (death knockback).
- **Bosses.** Enemy sync is generic (the runner runs the boss, the others show a puppet; hits, deaths, effects,
  handoffs). Boss *arenas* are generic too (`Sync/BossArena.cs`): every MonoBehaviour with the boss cinematics'
  shape (`boss`, `door`, `gameArea`, `toTeleportPlayer`, `hasBeenDone`, `HandleEvent(string)`, `ResetToLastState`;
  `fightConfiner` if it has one) is patched at startup, so a new boss of that make gets, with no code: its
  `HandleEvent` relayed from the runner to everyone in the arena (per-boss table `BossArena.Known`: what each event
  does to a player there, and what the runner does if it's elsewhere; unknown bosses: "ScreenShake" shakes),
  players who die in its fight put back in, players whose own intro hasn't played (late, or their trigger never fired) joining a fight another player's intro started (in the arena, door shut, music and bar), the party wipe's rewind (it's a resettable) and its log line. Each boss's
  own mechanics still need a handler: the Matriarch's stage and web (`BossSync`) and eggs (`EggSync`); see
  [EA-PORT.md](EA-PORT.md) for what the Heartwood would need.
- **World objects** go through one framework: a handler per kind captures/applies state using the game's own methods;
  changes are broadcast, decisions (who gets an item, shop stock) are intents to the scene's runner, late arrivals get
  snapshots, and the host keeps a table of object states for scenes it isn't in (`<world>_mp_objects.txt`).
- **Drops and the world's small reactions.** Items a player throws away from its inventory (`Inventory.DisposeItem`,
  gear to the bin) are network drops in both loot modes (`WorldDrops.PlayerDrop`): the runner spawns them, the first
  to grab one gets it; other drops follow the loot mode. A puppet has no `PlayerCenter` trigger (that tag picks things
  up and opens interactions), so `PuppetTouch` queries what its center would touch and calls grass sway, critter
  flight and ghost vanish itself. A copy of a teammate's heal or buff area works on our own character only (its own
  tag filter applies; enemies are the caster's). `RandomEventSelector` picks by scene path on placed objects (enemy
  colours, egg looks), so every copy shows the same.
- **Story and world scripts** (`Sync/Objects/ScriptSync.cs`): a change is sent if our player did it
  (`ObjectSync.LocalAction`) or we run the scene. Scripts that run on one copy because of its player (a dialogue's
  events and line triggers, lore pages, trigger zones our player walks into, timed triggers, entrances, tutorial
  windows' close events, an item's dialogue) are scopes (`ScriptScope`) that count as the player's doing, also watch
  the plain `SetActive` calls in their events (kind `scripted`), and name the source of character rewards
  (`Sync/CharacterRewards.cs`: items, classes and XP outside quests go to every player once, like quest rewards).
  Teleporters' leave/arrive and rooms' enter/exit events only share their one-way `SetActive` calls (nothing else of
  theirs switches the object back; a room's: outside the room), never the move, camera or music.
  Story flags only grow; one that arrives re-runs the loaded scene's `CheckGameBool`s that depend on it, and a merged
  quest the `ObjectiveChecker`s / `QuestChecker`s whose answer it changed (`Sync/LiveFlags.cs`; both wait for a
  cutscene playing here). Only saved dialogue bools are shared. A recipe another player learned shows the game's
  notification (`Sync/WorldNotices.cs`; not during a join, bursts cut short). The clock is the host's
  (`Sync/WorldClock.cs`: guests' scripts setting it are forwarded). Buying Tinetia's map unlocks is decided by the host
  (`Sync/CartographerSync.cs`).
- **NPC talk** (`Sync/Objects/NpcTalkSync.cs`): one talker at a time; while someone talks, every copy turns the NPC
  to them and holds its bubble on the talking sprite, and puppets ignore the runner's facing (`EnemySync.HoldFacing`).
- **Saves.** The game saves on a scene change and on quit only; in co-op items change hands while everyone stays put.
  After a stash move, a key item, a guest's purchase, shared loot or crafting order, both sides save what they own
  within seconds (`Sync/SaveSync.cs`: debounced, at most every 5 s, only at safe moments: no load, switch, cutscene,
  dialogue, open chest or shop); whenever the host's world reaches the disk it tells the guests (`SaveNow`) and they
  save their characters; the host also saves every 5 minutes while others are in. The mod's side files
  (`<save>_mp_*`) are deleted with their save, cleared when a new save takes the slot, and world side files start
  with the world they belong to (`Sync/SideFiles.cs`); a new character's creation time gets a random fraction of a
  second (the game stamps only the date, so a recreated character looked like the old one to its records).
- **Other mods** (`Api/`, [API.md](API.md)): three message ids for everything they do (`ModMessage` channels,
  `ModState` stores and declared mods, `ModSpawn` spawned objects), all through the host, which checks each channel's
  limits for each guest and writes the sender's id itself. Their object kinds and spawned objects ride on `ObjectSync`
  (spawned ones by net id); their world data is `<world>_mp_mods.txt`. The hello carries each side's
  `[MultiplayerMod]` mods: an `Everyone` mod missing or of another version is refused with a readable reason. Every
  call into a mod is try/caught and logged under its GUID.
- **Reconnect.** A guest that loses the host keeps playing in the host's world, reconnects with its old player id,
  receives the world again and stays where it was; after 60 s it gives up and goes home.
- **One session, one world.** The game's Main Menu button (pause menu, language change) leaves the session first, as
  MULTIPLAYER > Leave does (it asks first; a guest goes to the menu with its character saved, the host ends the session
  for everyone); the main menu loading from a game with a session up ends it too. `WorldState` sends and merges nothing
  unless this copy plays the session's world (a guest: the host's, under the throw-away name; the host: not on the menu).

## Testing

Every feature has a self-test scenario that runs the real game, headless, several copies at a time on one PC.

```powershell
.\scripts\build.ps1
.\scripts\test-all.ps1 -Quick                            # the everyday check (~5 min): every major system
.\scripts\test-all.ps1 -Changed                          # what the changes since main touch (says why), and -Quick
.\scripts\test-all.ps1                                   # every scenario (before merging to main)
.\scripts\test-all.ps1 -Only presence,doors -Lag "60,3"  # some, with ~120 ms round trip and 3% loss
.\scripts\test-all.ps1 -Transport steamip                # over Steam Networking Sockets (by IP)
.\scripts\test-all.ps1 -Plan                             # only list the runs (which scenarios share a boot)
.\scripts\test-all.ps1 -Shuffle                          # chains in a random order (prints its -Seed), to catch leaks
.\scripts\run-pair.ps1 combat -Scene TutorialArea -Headless -Fresh     # one host + one guest
.\scripts\run-pair.ps1 crowd -Scene TutorialArea -Headless -Guests 3   # four players
.\scripts\run-test.ps1 dump -Scene TutorialArea                        # one copy
.\scripts\run-test.ps1 worldsurvey -Scene Town                         # quests' objectives, teleporter/room/NPC wiring
```

Results land in `build\test\<scenario>-host|guest|guest2...\`: `mp.log` (everything the mod logged), `stdout.txt`
(thread dumps if a copy hangs). Each check prints `[MP] CHECK ok|FAIL ... @<seconds>`, each scenario ends with
`[MP] RESULT` and `[MP] TIME <scenario> <seconds>`; `test-all` ends with a table per scenario (its seconds) and per
run (wall clock, when the last copy got in game, the scenarios' own seconds) and the slowest scenarios.

How the suite runs (`test-all.ps1`: two runs at once by default, never more than `-MaxCopies` (5) game copies of
~3 GB each, and a new one only with `-MinFreeGB` (3) to spare on top of ~3.2 GB per new copy; never held back when nothing runs):

- **One list.** Each scenario says what it needs in its attribute, `[Scenario("name", Timeout = 200, ...)]`, on one
  line; `test-all.ps1` reads them from the source (no table to keep). `Timeout` (seconds; none: not in the suite),
  `Players` (2 by default), `Start` (below), `Scene` (`"Town"`), `Switches` (its `--mp-*` switches while it runs,
  `"loot=shared estate keep-playing"`), `Quick` (in `-Quick`), `Fresh = false` (goes on in the last scenario's world
  when every copy is in its scene: only the player and the harness start over), `Covers` (paths it tests beyond the
  types its file names, for `-Changed`), `Isolate = "name"` (a run of its own, shared with the same name: for a
  scenario whose start inside a chain isn't proven, or that leaves the session unusable).
- **Chains.** The scenarios with the same player count share boots: as many per run as fit `-Budget` seconds of
  their time limits, each in a fresh world (`DevHarness.FreshWorld`), no restart (93 scenarios in 14 runs). Before
  each one the host sends its rules for it again (loot mode, estate, keep playing, teleporting: `SelfTest.SyncRules`),
  and a `Scene = "Town"` one gets its fresh world there.
- **Starts.** `Start = "boot"`: right after the game started (first of its run). `"own-world"`: the guests play their
  own world and join during the scenario; `"menu"`: they join from the main menu (later in a chain they leave and
  come back from there: `SelfTest.GuestsLeave`). `"menus"`: every copy on the main menu (a run of its own). A
  scenario that ends the session for someone (a guest leaves or is kicked, the host drops) is followed by everyone
  coming back the way a player would (`SelfTest.EnsureSession`): the host hosts again, a guest joins from where it is,
  with its old player id.
- **Boots.** The guests load their own world while the host loads (`--mp-join-later`), then join (`-NoFastJoin`: from
  the main menu).
- **Quick moves.** While the harness itself moves the game (the boot's first load, a fresh world, the way back to
  the chain's start) its fades take ~0.1 s, and frames aren't capped during scene loads (`--mp-slow-loads`: off).
  `-TimeScale 2` (`--mp-timescale`) runs the game faster; the mod's own timers and the tests' waits stay real time.
- **Affected tests.** `-Changed [-Base main]` picks the scenarios whose file changed, whose file names a type declared
  in a changed file (one hop more for `Patches/` and `UI/`, and by name: `DeathPatches` → `Deaths`...), or whose
  `Covers` match, plus `-Quick`; a change to the harness or the core (Session, MPRunner, Args, Net/, Presence,
  GuestWorld) runs everything. `-Files a,b` instead of git's list shows what a change would run.
- **Nothing carries over.** Before the reload, whatever is going ends first (a teleport's fade, a death) and nothing
  stays open (chests, the stash, panels, conversations); after it the player is a new game's (no locks, effects or
  invincibility, full health) and so is the mod's static state (chat lines, death announcements, aim trails, test
  switches): `Dev/DevHarness.Reset.cs`. `-Shuffle [-Seed n]` plays the chains in a random order to catch what still
  leaks; give the seed to repeat a run.
- **Time limits.** Each scenario has its own (`Timeout`): one that hangs or throws fails alone and the chain goes on;
  if the session can't be put back together, the rest of the chain fails at once. A barrier waits 15 s unless told more (scene
  loads, travel, fights); one that a peer never reaches, a copy found in another scenario, or no session for 30 s
  aborts the scenario on every copy at once (`[MP] ABORT`), and the chain goes on in step. Between two scenarios every copy
  gets back into the session and past "scenario-end", and the guests start the next one only on the host's "go"
  (`SelfTest.InStep`): one bad scenario costs that scenario, not the rest of the chain.
- **Fast deaths.** A scenario that doesn't look at the death animation or the death screen sets
  `DevHarness.FastDeath = true` at its start (`--mp-fast-death`: all of them): the game's waits in a death (the
  death animation, the fades, the respawn) are 4x shorter, and every step still runs in order (`Dev/FastDeath.cs`).
- **Waits.** Wait for what a check looks at (`WaitFor(cond, timeout)`, a peer's `#key=value`, a `Barrier`), not a
  fixed pause; keep a fixed one only to prove that something does *not* happen (and say how long it must be). A pause
  before a screenshot goes in `DevHarness.Screenshot(name, settle)`, skipped headless.

Switches (`--mp-*`, see `Args.cs`): `profile=X` (saves in `Saves_X`), `autostart[=Scene]` (skips the menus; makes a
character and world if needed), `host`, `join=ip:port|lan|lobby`, `join-later`, `transport=lan|steam|steamip`,
`test=scenario[,scenario...]`, `players=N`, `lag=ms[,loss%]`, `loot=shared|instanced`, `reconnect=seconds`,
`skip-dialogue`, `window=WxH`, `clean` (no status line), `timescale=N`, `mute`, `quit=s`, `estate` (host: estate
unlocked), `keep-playing` (host: skip the demo's end lock), `netstats` (bytes/s per message type in the log
every 30 s; the F8 window always has the table), `notrim` (bandwidth trims off, for comparisons),
`scenario-args=name:key=value/key;name2:...` (a chain's per-scenario switches, `scenario-timeout=s` among them),
`fast-death`, `hide-mod=<guid>[,...]` / `guest-hide-mod=<guid>` (this copy / the guests act as if they didn't have
that mod: both copies run from one game folder).

Other mods' scenarios: `[MpScenario(...)]` in the [emberville-mods](https://github.com/HardHeadHackerHead/emberville-mods) repo's `mods\**\*.cs` (Party Tricks'
`partytricks-*`; clone it next to this repo, or set `EMB_MODS`), found in their DLLs by
the harness and in their sources by `test-all.ps1`, which deploys them (`build.ps1 -WithMods`). After a plain
`build.ps1`, run them with `build.ps1 -WithMods` first.

Bad input: the host checks every guest packet against `Net/NetGuard.cs` (known type, who may send it, size, rate per
type and in total) before relaying or handling it; junk is dropped and logged rate-limited, a guest that keeps sending
it is kicked with a reason. `fuzz` and `netstats` are the scenarios for this.

README screenshots are staged by scenarios too (windowed):

```powershell
.\scripts\run-test.ps1 menu -NoAutostart -Extra @("--mp-window=1920x1080","--mp-clean")
.\scripts\run-pair.ps1 showcase -Scene TutorialArea -Fresh -Extra @("--mp-window=1920x1080","--mp-clean")
```

## Parallel work

Several checkouts can build and test at once (git worktrees, for example). Give each its own copy of the game, ports
and save profiles, set in every shell:

```powershell
$env:EMB_GAMEDIR  = "<a copy of the game folder>"   # the build deploys here and the tests run it; never the Steam install
$env:EMB_TAG      = "x1"                            # prefix of this checkout's test save profiles
$env:EMB_PORTBASE = "27100"                         # its UDP ports (+0..+20)
$env:EMB_MODS     = "<emberville-mods checkout>"    # when it isn't next to this checkout
```

`.\scripts\build.ps1 -NoDeploy` compiles without touching any game folder.

## Gotchas

- **Leaving the main menu too early deadlocks** the next scene (Unity Localization asks for strings synchronously while
  initialization runs). Anything that builds localized UI or leaves the menu waits for
  `LocalizationSettings.InitializationOperation`; cloned UI drops its `LocalizeStringEvent`.
- **Two copies starting at the same instant**: BepInEx rewrites `BepInEx.cfg` at startup and the second dies with a
  sharing violation. Starts are staggered; the mod's config only saves on purpose; the disk log is off.
- **Town is the end of the demo**: Gumu's intro leaves the player frozen and shows "thank you for playing" (on later
  visits a `TimedTrigger` calls `CutsceneGumu.DemoLock` and shows it), so most tests run in `TutorialArea`.
  `--mp-keep-playing` (or the host option) skips it.
- **The demo has no estate**: the scene its teleporters lead to (`CreativeArea`) isn't in the build, and no scene or
  Resources asset has a `PlaceableController`, `FarmingManager`, `TilesetDrawerManager`, soil or placeable database.
  The estate tests build a small estate from the game's own classes (`Dev/EstateFixture.cs`).
- **Writing a blackboard variable wakes an enemy's graph**: only live, running enemies are retargeted.
- **Death rewinds the room** (`GameManager.ResetScene`): replaced in co-op, except for a party wipe (everyone in the
  scene down: the runner calls it, every copy rewinds as its player gets up). Death screen and pause set
  `Time.timeScale = 0`: kept at 1 while others are in the scene.
- **`Game.InGame` is false on the death screen** (`gameStarted` is false from `ResetScene` until the respawn): what
  runs a scene for the others (presence, scene authority, enemies, world objects) goes by `Game.InScene` (in the game,
  or lying dead in it), so a dead runner keeps running its scene instead of handing it over for its death screen.
  Who's down also comes from the death notices (`Deaths`: died / back up).
- **Anything that changes a dead player's state revives them** halfway through the death (the game's
  `RemoveStaticStack` goes to idle whenever the stack is empty: a panel or tutorial closing does it): in co-op the
  local dead character is only released by its respawn (`DeadStaysDead`: the static-stack, `Static` and stun entry points are guarded).
- The game **saves on every scene change**: guests' world writes are blocked, and the save during a world swap is skipped.
  Our own saves (`SaveSync`) never run with a chest or shop open: its items sit in the inventory then and its own
  list is only written back on close, so a save would keep them in both.
- The game **reuses a deleted save's slot name** and only deletes its own files: anything keyed on the save's file name
  must go with it (`SideFiles`). Characters are stamped with the date only (`DateTime.Today`).
- Many game methods finish after an animation (`await`), so object sync reports the **destination** state.
- One Steam account can't P2P to itself: same-PC tests use Steam sockets by IP (`steamip`).
- **The game switches its input maps itself** (dialogue, cutscenes, panels, the death fade). Our text fields switch
  all of them off and, when they close, give each action the state the game asked for meanwhile (`UI/InputBlock`
  records the game's `Enable`/`Disable` calls while it blocks): never re-enable a map unconditionally.
- **Unity's audio is off** in this game (everything goes through Wwise): voice plays through Windows' `waveOut`.
- **Puppets are untagged** (the "Player" tag finds the player and drives interactions): anything the game keeps off
  its player by that tag needs `FriendlyFire.IgnoredLikePlayer` for puppets.
- **Enemy lifesteal never procs in the game itself**: `Attack.lifeStealTag` needs `attackDamage.Result == Success`,
  which `CombatSystem.GetDamageData` never sets (plain hits stay `None`). Nothing to mirror for hits on teammates.
- Hangs: `Watchdog` calls Mono's `mono_threads_request_thread_dump`; the stacks go to stdout.

## Releases

`.\scripts\package.ps1` builds two downloads in `build\`:

- `EmbervilleMP-<version>.zip`: the mod alone, for players who have BepInEx 5.
- `EmbervilleMP-<version>-with-BepInEx.zip`: BepInEx 5.4.23.5 plus the mod, unzipped into the game folder.

Both take an explicit list (`EmbervilleMP.dll`, `LiteNetLib.dll`, `Steamworks.NET.dll`, `steam_api64.dll`) and fail if
a mod built on the API got in. Those are released on their own, from [emberville-mods](https://github.com/HardHeadHackerHead/emberville-mods):
`.\scripts\package.ps1 -MultiplayerVersion <this version>` there makes their zips and mod-site packages.

For the mod sites (Hexium now, Thunderstore later): `python scripts/store.py` packages `build\store\Quads_Lab-<name>-<version>.zip`
from `store/` and checks it (`--mod <Mod>`: a mod from emberville-mods), `--publish` uploads it to Hexium (`HEXIUM_AUTH_TOKEN`;
a published version can never be changed, only followed by a higher one). Every package carries its `LICENSE.md`.

Bump `Plugin.Version` for every release, and `Protocol.Version` whenever a message layout changes (players on
different protocol versions get a clear "update the mod" message instead of a broken session). The public API has its
own `Multiplayer.ApiVersion` (1): nothing in it may change while it stays the same.

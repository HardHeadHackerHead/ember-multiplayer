# Porting to Early Access (2026-10-27)

Emberville Early Access is a **different build and a different Steam app** from the demo the mod targets. This page
is the day-one checklist, the tool that tells us within minutes what broke (`scripts\ea-check.ps1`), and the list of
demo-only facts in our code.

- [Before the release](#before-the-release)
- [Day one, step by step](#day-one-step-by-step)
- [ea-check](#ea-check)
- [What will likely change](#what-will-likely-change)
- [Demo-only assumptions in our code](#demo-only-assumptions-in-our-code)
- [Keep supporting the demo?](#keep-supporting-the-demo)

## Before the release

- **Keep an untouched copy of the demo.** `ea-check` compares against the build the mod was made for. If Steam
  updates the demo when EA ships, use a copy that Steam doesn't manage as the baseline: `-BaselineDir <untouched copy of the demo>`.
  `docs\EA-INVENTORY.md` is a committed snapshot of every demo signature we use (regenerate it with `-Snapshot`).
- `.\scripts\ea-check.ps1` must report **no problems** against the demo, and `.\scripts\ea-check.ps1 -Prove` must
  report **PROVE OK**: every kind of injected break is caught. Both were true on 2026-10-20.

## Day one, step by step

1. **Install EA from Steam.** Look at the folder before touching it:
   - `<Game>_Data\Managed\Assembly-CSharp.dll` exists and there's a `MonoBleedingEdge` folder: it's a Mono build, so
     BepInEx 5 works. If there's `GameAssembly.dll` and `il2cpp_data` instead, it's IL2CPP: **stop**. BepInEx 5
     can't load there; that means a port to BepInEx 6 IL2CPP (a much bigger job).
   - The exe and data folder names. The build assumes `Emberville.exe` (`scripts\launch.ps1:38`) and
     `Emberville_Data` (`src\EmbervilleMP\EmbervilleMP.csproj:12`). `ea-check` finds any `*_Data` folder by itself.
   - The Steam app id: `steamapps\appmanifest_<id>.acf` with `"installdir" "<the EA folder>"`.
2. **Run the checker against the untouched install.** It only reads the install:
   ```powershell
   .\scripts\ea-check.ps1 -GameDir "D:\SteamLibrary\steamapps\common\<EA folder>"
   ```
   Read `build\ea-check\<EA folder>\check.md`. Problems are sorted by impact (Critical > High > Medium,
   runtime before test-only), and each one names its feature and the file:line in our code that uses it. The
   **Notes** section lists new and removed scenes and assemblies.
3. **Make a test copy** of the EA build, for example `<games>\ea1` (and more for parallel checkouts; see "Parallel work"
   in [DEVELOPMENT.md](DEVELOPMENT.md)). Never test in the Steam install. Unzip `downloads\BepInEx_win_x64_5.4.23.5.zip` into
   the copy and start it once, so BepInEx sets itself up.
4. **Decompile the new build** and diff it against the demo's:
   ```powershell
   ilspycmd -p -o notes\decompiled-ea "<games>\ea1\<Game>_Data\Managed\Assembly-CSharp.dll"
   git diff --no-index --stat notes\decompiled notes\decompiled-ea
   ```
   This is a local reference only and is never committed (see `.gitignore`).
5. **Fix in the order of `check.md`:**
   - **Critical**: a type moved, renamed or changed kind, or a game interface we implement gained a member. Our class
     fails to load, and so does anything that touches it.
   - **High**:
     - A hook target is missing or now ambiguous. Ambiguous means a new overload appeared, so give argument types in
       `[HarmonyPatch]`.
     - A parameter our patch binds by name was renamed (Harmony refuses the whole patch class).
     - A method or field we call changed (a `MissingMethodException` when that code first runs).
     - A lookup by name now fails.
     - An enum member changed value (see [BoolID](#enum-values-boolid-and-friends)).
   - **Medium**:
     - A transpiler finds nothing to replace.
     - A scene name is gone.
     - A default argument value changed (the old value is baked into our IL).
     - A patch parameter's type changed.
6. **Rebuild against EA**, compile only:
   ```powershell
   $env:EMB_GAMEDIR = "<games>\ea1"; .\scripts\build.ps1 -NoDeploy
   ```
   Then check the EA build against itself; it must report no problems:
   ```powershell
   .\scripts\ea-check.ps1 -BaselineDir <games>\ea1
   ```
   The compiler catches everything `ea-check` reports, and also renamed enum members and fields.
7. **Re-survey the scenes**: `run-test.ps1 survey -Scene <scene>`, `run-test.ps1 estatesurvey -Scene <hub>` and
   `run-test.ps1 dump`, for the new hub, the dungeons and the town. Update the test constants listed
   [below](#test-only-code-dev) (rooms, coordinates, BoolIDs). Most tests start in `TutorialArea`. Check that
   it still exists and that `003_Vitromotus`, `004_Gather` and `001_Barrack` are still its rooms.
8. **Run `test-all.ps1`** on the EA copy (`EMB_GAMEDIR`). Every scenario first checks "every game hook in place";
   a hook that failed shows up there and as `[patch]` in `mp.log`.
9. **Steam**: run `run-test.ps1 steamcheck`. The app id comes from Steam (see [App id](#steam-app-id)), so nothing
   needs to change for EA. The lobby filter `emberville_mp` and the protocol version keep demo and EA lobbies apart
   anyway, because Steam lobbies are per app. Then repeat A7 (the real two-account test) on EA.
10. **Release**: bump `Plugin.Version`, and `Protocol.Version` too, since demo and EA players must not meet over
    LAN/IP (see [game version](#runtime-code)). Update the README (requirements, store link, "Built for the free
    Emberville Demo"), and `ROADMAP.md` Phase P.

## ea-check

```powershell
.\scripts\ea-check.ps1                          # inventory + self-check against the demo: expect "No problems"
.\scripts\ea-check.ps1 -GameDir <other build>   # what's missing or changed there (exit 1 if anything)
.\scripts\ea-check.ps1 -Prove                   # inject known breaks into a copy of the demo DLLs: expect "PROVE OK"
.\scripts\ea-check.ps1 -Snapshot                # also refresh docs\EA-INVENTORY.md
```

Options: `-BaselineDir` sets the build the plugin is compiled against. It defaults to `EMB_GAMEDIR`, else the
demo's Steam folder. `-NoBuild` and `-Plugin <dll>` pick the plugin; `-Out` sets the report folder.

Reports land in `build\ea-check\<name>\`:
- `inventory.md`: every dependency, grouped by feature and source file.
- `check.md`: the problems, most severe first.

The script builds `tools\EaCheck` (a .NET 8 console tool on Mono.Cecil; its `bin/obj` stay out of git) and the
plugin (`build.ps1 -NoDeploy`). It reads game folders and never writes into them.

What it reads from `EmbervilleMP.dll`, using the `.pdb` for file:line:

| Kind | What's checked against the other build |
|---|---|
| Hooks | Every `[HarmonyPatch]` class and every `TargetMethod(s)`, resolved the way Harmony does (declared only for attributes; overloads count as ambiguous). Every parameter our Prefix/Postfix/Finalizer binds by name (`key`, `damageData`, `___field`, `__result`...) must exist in the target. Transpilers: the target's body must still use what the transpiler replaces (`Time.time` in `ShooterTrap.LateUpdate`). |
| Lookups by name | `AccessTools.Method/Field/Property...`, `FieldRefAccess`, `Type.GetMethod/GetField...` with a `typeof` and a string. `Traverse` and `obj.GetType().GetMethod(...)` are listed as dynamic; the members they mean are written in `tools\EaCheck\ea-hints.txt` and checked from there. |
| Methods, fields, types | Every member reference into a game assembly (Assembly-CSharp, UnityEngine.\*, Unity.\*, UniTask, R3, Wwise...): exists with the same signature and static-ness; types keep their kind (class/struct/enum/interface); fields keep their type; optional parameters keep their default (C# copies it into our IL). |
| Implements | Our classes implementing game interfaces (`IDamageable`, `ICenterOfMass`) still implement every member. |
| Enum values | C# compiles `BoolID.MetAdeos` into a number. Every `Enum.Member` in our sources is looked up and its value compared; `(Enum)123` literals report what the number now means. |
| Scenes | Every string in our IL that is a scene name of the baseline must be a scene of the other build (from `globalgamemanagers`). |

Inventory on 2026-10-20 (demo; `docs\EA-INVENTORY.md` has the full list):

- **1282 dependencies**:
  - 80 hook classes;
  - 12 lookups by name (2 with a static type and name, 4 dynamic ones, and the 6 `ea-hints.txt` members that cover those 4);
  - 554 methods, 293 fields and 250 types;
  - 4 interface implementations;
  - 84 enum values;
  - 5 scenes.
- **By assembly**: Assembly-CSharp 796, UnityEngine.CoreModule 268, the rest spread over 20 other assemblies.
- **Hooks by feature**:
  - Combat: enemies 11; room events & waves 10.
  - Crafting 6.
  - Doors 5; switches/plates 5; breakables 5; loot 5.
  - Chests 4; traps 4; shared world & saves 4.
  - Boss eggs 3; shops 3; NPC talk lock 3.
  - Story 2; secret rooms 2; repairs 2.
  - Gathering, death, splash, framework, puppets: 1 each.
  - Tests: 1.

What it can't see:
- Changes inside game methods that keep their signature (behaviour, timing, new `await`s).
- Scene contents: object names, hierarchy paths, animator states, prefab names.
- Save format changes.
- Members reached dynamically that aren't in `ea-hints.txt`. These are listed in `check.md` as "unchecked dynamic lookup"; add them to the file.

The surveys and `test-all.ps1` cover those.

Proof (2026-10-20): `-Prove` copies the demo's `Managed` folder and `globalgamemanagers` into `build\ea-check\prove-game`
and applies 12 breaks to the copy:
- `GateDoor.Open` renamed;
- a `Chest.CloseChest` overload added;
- `DialogueTrigger.SetVariableValue`'s `key` renamed;
- `SpiderEgg` renamed;
- `Enemy.HealthChanged` renamed;
- `RepairableBuilding.transitionSfx` renamed;
- `BoolID.MetAtwor` and `DoorState.Opened` renumbered;
- `ShooterTrap.LateUpdate` switched to `Time.unscaledTime`;
- `CallScene`'s default changed;
- a member added to `IDamageable`;
- the `TutorialArea` scene renamed.

The checker reported all of them as 18 problems (4 critical, 11 high, 3 medium), each with its feature, and
reported none against the untouched demo or an identical copy.

## What will likely change

- **Procedural dungeons (`DungeonScene`)**: not in the demo. Rooms are generated, so object identity by scene path
  (`NetIds.ScenePath`, the chest/loot keys in `LootRules.ChestKey`) may differ between players unless everyone uses
  the same seed and generation order. Roadmap D6. Survey first, then check that `NetIds` produces the same ids on
  host and guest in a generated dungeon.
- **Farming, building, the estate, portals (Phase D)**: the demo has no instances. `DestructedPortal` sync is written
  but untested, and the estate survey (`estatesurvey`) found nothing to test. Expect new hooks.
- **Fishing**: `PuppetBuilder.DropChildren` strips a `Fishing` child from puppets. Fishing gameplay isn't synced at
  all yet.
- **Town is no longer the end**: the demo's Gumu intro calls `DemoLock`, and `SelfTest.Stations` sets
  `BoolID.GumuIntroductionDone` to get past it. In EA the town is probably reachable normally, so `shops` and
  `stations` can run without the workaround.
- **Saves carry over** from the demo to EA. Our side files `<world>_mp_objects.txt` and `<world>_mp_loot.txt` are
  keyed by scene path hashes and raw enum numbers. If EA changes scene hierarchies or enums, old entries apply to
  the wrong thing or to nothing. Consider a version tag in those files, or drop them on a game version change.
- **New enemy types and bosses**: enemy sync is generic, and so are boss arenas (`Sync/BossArena.cs`, found by
  the cinematic's shape: events relayed to the arena, back in after a death, the party wipe's rewind). Check the log
  line `[boss] Boss arenas: ...` lists the EA bosses, and add each one's events to `BossArena.Known` (read its
  cinematic's `HandleEvent`). Boss mechanics are per boss (the Matriarch: `BossSync` stage + web, `EggSync`). The
  **Heartwood** (`Heartwood.cs`, `HeartwoodBossCinematic.cs`, `HeartwoodSpike.cs`, `HeartwoodTree.cs`; in the demo
  build but in no demo scene) would need, from reading its code:
  - *Arena*: automatic (its `HandleEvent` only shakes on "ScreenShake": in the table). Defeat flag
    `BoolID.DefeatedHeartwood` is a story flag (shared already).
  - *Stage*: blackboard `Stage` (BossStage, set to Stage1 in `OnEnable`), as the Matriarch's: `BossSync`'s stage
    relay/inheritance is written for `SpiderMatriarch`; generalize it to any boss with a `Stage` variable.
  - *Grab*: `PerformGrabAttack` aims its hand at a random `PlayerMaster` in the scene (only the runner's own: the
    others' puppets have none) and `GrabHit` hides, stuns (1 s) and moves the players in the hand to
    `transportPosition`. Needs: the hand aimed at any player (puppets), and a "grabbed" message to the grabbed
    player's copy (hide, stun, move there).
  - *Stab*: `HasPlayerInStabPosition` / the stab colliders only count `PlayerMaster`s (the runner's player): the stab
    has to see puppets too (`RemotePlayerTarget`); its hits on puppets then go the usual forwarded way.
  - *Zoning spikes* (arms at random angles) and *web prefabs A/B/C*: shown on the runner only; need their angles /
    which one relayed (visuals; their hits on puppets are forwarded already).
  - *Trees* (`HeartwoodTree`, "awaken" attack): the runner fires pooled projectiles from random trees; the others
    need them shown (as other enemy projectiles) and the tree attack animation.
  - *Spikes* (`HeartwoodSpike`: resettable, a player's hit counters it back at the boss): a hit on one from a
    non-runner must go to the runner (like `EggSync` hits).
- **Unity version**: if EA updates Unity, `ea-check` covers the UnityEngine.\* references too, and the `.csproj`
  reference list may need new module DLLs.

## Demo-only assumptions in our code

Audit of `src\EmbervilleMP` on 2026-10-20. **Runtime** code ships to players; **test-only** code is `Dev\**`, reached
only with `--mp-test`, `--mp-autostart` or `--mp-profile`.

### Changed on this branch (behaviour on the demo unchanged; for the lead to test)

| Where | Change |
|---|---|
| `Steam/SteamBootstrap.cs:12-56` | The app id used when the game isn't started by Steam is no longer always 5355880. Lookup order: `SteamAppId` env (Steam launch) > `steam_appid.txt` in the game folder > the Steam library's `appmanifest_<id>.acf` whose `installdir` is this game folder > `DemoAppId` (5355880). Started from the demo's Steam folder this gives 5355880 (`appmanifest_5355880.acf`), from a test copy the fallback 5355880, from the EA folder EA's id. It's logged: `[steam] Not started by Steam: app id N (from ...)`. |
| `Dev/SelfTest.Steam.cs:56` | `steamcheck` compares against that resolved id ("running as this game's Steam app"). |
| `Game.cs` | `Game.MainMenuScene` / `Game.SplashScene` constants. `Game.FallbackStartScene` reads the default of `SceneLoader.CallScene`'s `fallbackStartScene` argument from the running game ("TutorialArea" in the demo), else "TutorialArea". `Game.FindUi(root, path)` does `root.Find(path)`, else finds the path's last name anywhere under root (with a warning). |
| `MPRunner.cs:96,313`, `Sync/GuestWorld.cs:126,136,246`, `UI/MainMenuMP.cs:52` | Use the scene constants (same strings). |
| `Sync/GuestWorld.cs:255` | Going home with no saved scene: `Game.FallbackStartScene` instead of the literal "TutorialArea" (the game's own fallback, the same value in the demo). |
| `UI/MainMenuMP.cs:80-81,95-96`, `UI/PauseMenuMP.cs:50-51` | Menu buttons and panels are found through `Game.FindUi` (same paths). If EA reshuffles the hierarchy, the MULTIPLAYER button is still found by name instead of the menu silently missing. |
| `Sync/EnemySync.cs:315-329` | `Enemy.HealthChanged` (`FieldRefAccess`) is looked up on first use, not in a static initializer. If EA renames it, guests' enemy health bars stop updating (logged once). Before, all of `EnemySync` failed to initialise. |

### Runtime code

| file:line | Value | Use | EA risk | Note |
|---|---|---|---|---|
| `Steam/SteamBootstrap.cs:13` | `DemoAppId = 5355880` | Fallback app id only (see above) | low | The only place the id appears in code. Other mentions are docs: README.md:91,239 (store links) and ROADMAP.md:40. Lobbies and friends use `SteamUtils.GetAppID()` (`SteamLobby.cs:167`, `MainMenuMP.cs:355`). |
| `Game.cs` | `"MainMenu"`, `"SplashScreen"` | Menu detection, going back to the menu, refusing snapshots taken outside play | low | One place to change if renamed. `ea-check` flags a missing scene. |
| `Game.cs` | `"TutorialArea"` | Last-resort fallback of `FallbackStartScene` | low | Only if the game's own default can't be read. |
| (compiled in) `SceneLoader.CallScene(scene, cb)` calls | `fallbackStartScene = "TutorialArea"` | The game's optional argument, copied into our IL by the compiler | low | `ea-check` reports a changed default; a rebuild picks it up. |
| `UI/MainMenuMP.cs:80,95,96,98,100` | `Content/Holder/Button_Options`, `Content/Holder/Button_Confirm`, `Title`, `Button_Back` | Cloning the menu's buttons and the exit panel | med | Found by name if moved. If renamed: `[menu] Couldn't add the multiplayer menu` in the log; F8 still works. |
| `UI/MainMenuMP.cs:124,125,151` | `Content/Logo Holder`, `Copyright`, `VersionNumber` | Logo badge and mod version line | med (cosmetic) | Null-safe: skipped if missing. |
| `UI/MainMenuMP.cs:136-144` | Badge colour, size, position (210,-95), 7° rotation | Fitted to the demo logo | cosmetic | Re-fit to the EA logo (`run-test.ps1 menu -NoAutostart` screenshot). |
| `UI/MainMenuMP.cs:70,209` | `(MainMenuState)100` | A state the game doesn't handle, to keep its Back/Esc out of ours | low | Breaks only if EA adds a state 100. `ea-check` lists what 100 means. |
| `UI/MainMenuMP.cs:224` | `"Space"` | Spacer kept when the panel is rebuilt | low | |
| `UI/PauseMenuMP.cs:50,61,87` | `Content/ButtonHolder/Button_Options`, `Button_Resume` | Pause menu MULTIPLAYER entry | med | Same as the main menu. |
| `Sync/PuppetBuilder.cs:15-17` | `BodyCollider`, `CenterTrigger`, `DamageableCollider`, `FarmingTriggers`, `NightLight`, `SpawnPoints`, `AnimTrappedLevel`, `TouchDamage`, `PlayerPortrait`, `Fishing`, `MinimapCenter`, `SchorchMarkSpawn`, `GoreMarkSpawn`, `OverHeadTextbox`, `Graphics/AttackHitbox`, `Graphics/Events` | Player-rig children removed from other players' puppets | med | Logic components are stripped generically anyway. A renamed or new child only leaves stray visuals (light, portrait, minimap marker). Check a puppet in EA and update the list. |
| `Sync/Presence.cs:82`, `Sync/RemotePlayer.cs:160` | `"Graphics"` | Root of the player's look (rig parts, colours, gear) | med | If renamed, looks aren't sent (`presence` test fails). |
| `Sync/RemotePlayer.cs:157` | `"Player_ExtraFxAnimations"` | Extra FX animator on puppets | low | Null-safe. |
| `Sync/RemotePlayerTarget.cs:31,33,44` | `"DamageableCollider"`, layer `"IDamageables"`, size 0.6×1.2 | Hurtbox enemies hit on a guest's puppet | low-med | The layer and size are only a fallback if the collider isn't found. |
| `Sync/HostWorld.cs:168` | Blackboard variable `"Player"` | Enemies' behaviour graph target (chase guests) | med | If renamed, enemies ignore guests (warning in the log). `combat` test. |
| `Sync/EnemySync.cs:420`, `Sync/Objects/TrapSync.cs:113` | `"Hit"` (sfx, animator trigger) | Guest-side hit sound, ground trap animation | low | Cosmetic. |
| `Sync/PlayerLook.cs:34` | `"Sprite-"` material prefix | Skips the game's shared default materials | low | Harmless if renamed (more materials synced). |
| `Sync/Objects/CraftingSync.cs:290,301,316,322`, `NpcTalkSync.cs:86`, `StoreSync.cs:294`, `TrapSync.cs:110,138,145` | Hooks on private methods by string: `Update`, `Finish`, `GiveXP`, `ForceCollect`, `OnEnded`, `Setup`, `OnEnable`, `LateUpdate` | Crafting, NPC lock, shops, traps | med | Checked by `ea-check` like every hook. |
| `Sync/WorldState.cs:289,304`, `Sync/EnemySync.cs:325`, `Sync/Objects/RepairSync.cs:104-107` | `OnEnemyDied`, `Item` (indexer), `HealthChanged`, `transitionSfx`/`transitionVfx`/`Post`/`Play` | Lookups by name | low-med | Checked by `ea-check` (the repair ones through `ea-hints.txt`). |
| `Sync/WorldState.cs:58,64`, `Sync/EnemySync.cs` (kill credit), `Sync/DamageCodec.cs` | `BoolID`, `EnemyType`, `DamageResult`... sent as numbers | Network messages | low | Both peers run the same build. Only a mixed demo/EA session would misread them. |
| `Sync/WorldState.cs:103-124` | `worldData.TinetiaUnlock` | Demo map-unlock flags, OR-merged | med | Content-specific; compile error if removed. |
| `Sync/Objects/ObjectSync.cs:286`, `Sync/LootRules.cs:109,133` | `<world>_mp_objects.txt`, `<world>_mp_loot.txt`, keyed by `scene:hash(path)` | Remembered object states and per-player chests | low-med | See "Saves carry over" above. |
| `Sync/Backups.cs:14,26` | `EmbervilleMP_Backups`; save files matched by `prefix + "."`/`"_"` | Backups before joining or hosting | low | Assumes `player_slotN.evp` / `world_slotN.ev?` naming. |
| `Net/Session.cs:219-221,299-303` | Handshake: `Protocol.Version` + `Plugin.Version` only | | **med** | **No game build check**: a demo player and an EA player could connect over LAN/IP. Suggest adding `Application.version` (or the Assembly-CSharp MVID) to the handshake with a clear refusal, plus a `Protocol.Version` bump. Not done here because it changes the protocol. |
| `Dev/Watchdog.cs:11` (started by `MPRunner.cs:39` every run) | `mono-2.0-bdwgc.dll` | Thread dumps on hangs | low | In try/catch. Only an IL2CPP build lacks it, and then BepInEx 5 doesn't load anyway. |
| `EmbervilleMP.csproj:11-12`, `scripts/build.ps1:4`, `launch.ps1:3,38`, `package.ps1:7` | `D:\...\Emberville Demo`, `Emberville_Data`, `Emberville.exe` | Build references, test launches, packaging | high (build) | `EMB_GAMEDIR` overrides the folder. If EA renames the exe or the data folder, change `Managed` in the csproj and the exe in `launch.ps1`. |

### Test-only code (`Dev\**`)

These need new values after the EA surveys. None of them ship behaviour to players.

| file:line | Value | Note |
|---|---|---|
| `Dev/SelfTest.Combat.cs:15` (used in Combat, Coop, Showcase, Travel) | `CombatArea = "003_Vitromotus"` | TutorialArea's combat room |
| `Dev/SelfTest.Combat.cs:66-67`, `Coop.cs:52-54,92`, `Travel.cs:37,73`, `Showcase.cs:30,44,77,79` | Positions such as (51.8,76.3), (58,86), (150,130), (52.5,76.6), (410,262) | Spawn spots in demo rooms |
| `Dev/SelfTest.Coop.cs:52`, `Stations.cs:239`, `Loot.cs:236` | `"004_Gather"` | Gathering room |
| `Dev/SelfTest.Loot.cs:31`, `Interactables.cs:309` | `"001_Barrack"`, (-104,70) | |
| `Dev/SelfTest.Interactables.cs:70,140,331` | `"07_"`, `"05_"`, `"05_Trap_Room2"`, `Mob_Glowbat` | Survey filters, trap room, enemy template |
| `Dev/SelfTest.Coop.cs:145-175`, `Showcase.cs:77-86` | `"SpiderMatriarch_Area"`, `MatriarchBossCinematic`, `"Boss"` | Boss tests |
| `Dev/SelfTest.Travel.cs:21,73-75`, `Stations.cs:200`, `DevHarness.cs:160` | `"TutorialArea"` | Autostart default and travel target. `test-all.ps1:14-37` uses `-Scene TutorialArea`; `run-pair.ps1:5` defaults to `Town`. |
| `Dev/SelfTest.Travel.cs:25-33` | `"VitromotusCrystal"`, `BoolID.CutsceneCrystal` | Hub travel |
| `Dev/SelfTest.Stations.cs:44-49,117`, `test-all.ps1:36` | `"Town"`, `BoolID.GumuIntroductionDone`, `BoolID.RepairablesActive`, `"Repairable_Cartographer"` | Gets past the demo lock |
| `Dev/SelfTest.Progress.cs:32,37,54,72`, `World.cs:27,54`, `Estate.cs:28`, `Survey.cs:43` | `BoolID.MetAtwor`, `DefeatedKaldhor`, `MetAdeos`, `PlaceablesActive`, `RepairablesActive`, `PortalRepairActive`, `CutsceneCrystal` | Story flags the tests set |
| `Dev/SelfTest.Steam.cs:56` | Resolved app id | See the changes above |
| `Dev/SelfTest.Menu.cs:38`, `Steam.cs:17` | `"WelcomePanel"` | Dismisses the welcome popup |
| `Dev/SelfTest.cs:170` | Animator trigger `"Wood"` | |
| `Dev/DevHarness.cs:16-17,110-115,132-138` | `player_slot1`, `world_slot1`; 8 customisation ints with demo ranges; `difficultySettings[1]` | Autostart character and world creation: check the EA character creator's option counts |

### Enum values (BoolID and friends)

C# compiles enum members into numbers, so the built mod keeps the demo's numbers until it's rebuilt. `ea-check`
compares all 84 members we use (the table in `EA-INVENTORY.md`). Runtime code uses no `BoolID` member by name: story
flags are synced generically as numbers. The 7 `BoolID`s are test-only. Runtime code does use members of
`DoorState`, `SwitchState`, `QuestState`, `DungeonRoomEvent.EventState`, `EnemyWaveHandler.waveState`,
`GatheringObject.GatherType`, `InventorySection`, `DamageResult`, `MainMenuState` and others. A renumbering there is
silent until rebuilt. Note: in the demo, `DefeatedSpiderMatriarch` and `MetRuton` share id 24 (ROADMAP B5).

## Keep supporting the demo?

Recommendation: **no, once EA works**. Ship one release for the demo (the current `0.x`), then move `main` to EA:
- The demo is a different app that players leave once EA is out, and the two can't play together.
- Supporting both doubles the test matrix (`test-all.ps1` per build) and needs per-build branches in hooks wherever
  EA changed something.

If both are needed for a while:
- Build twice (`EMB_GAMEDIR` = demo or EA).
- Pick per build at runtime from the app id (`SteamBootstrap.AppId`) or `Application.version`.
- Keep `ea-check` green against both (`-BaselineDir` demo, `-GameDir` EA, and the other way round).
- Add the game build to the handshake (see above) so mixed sessions are refused clearly.

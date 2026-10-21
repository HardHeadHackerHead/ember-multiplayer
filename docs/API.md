# Making mods for Ember Multiplayer

Ember Multiplayer has a public API for other BepInEx mods: who's in the session, who decides what, your own
messages between the players, shared data saved with the host's world, world objects and spawned objects that every
player sees the same, and a compatibility check when someone joins. Everything is in one namespace,
`EmbervilleMP.Api`, entry point `Multiplayer`.

Two mods built on it are good places to copy from, both in [emberville-mods](https://github.com/HardHeadHackerHead/emberville-mods): **Party Tricks**
([`mods/PartyTricks`](https://github.com/HardHeadHackerHead/emberville-mods/tree/main/mods/PartyTricks): cheers and campfires, uses almost everything here) and
**Damage Colors** ([`mods/DamageColors`](https://github.com/HardHeadHackerHead/emberville-mods/tree/main/mods/DamageColors): a 30-line client-only mod with a soft
dependency).

- [Guarantees](#guarantees)
- [Setup](#setup)
- [Compatibility](#compatibility)
- [The session and its players](#the-session-and-its-players)
- [Who decides](#who-decides)
- [Events](#events)
- [Channels: your own messages](#channels-your-own-messages)
- [Shared data](#shared-data)
- [World objects](#world-objects)
- [Spawned objects](#spawned-objects)
- [Helpers](#helpers)
- [Testing your mod](#testing-your-mod)
- [Releasing your mod](#releasing-your-mod)

## Guarantees

- **`Multiplayer.ApiVersion` is 1.** Nothing in it changes meaning or signature while it stays 1; additions may come.
  Check it if you need something added later.
- **Your bugs stay yours.** Every call into your code (event handlers, channel handlers, object kinds, spawn
  factories) is try/caught. The exception is logged under your mod's GUID (`[api] <guid>: ... failed`, a few full
  reports, then counts), and the session goes on. Each event subscriber runs on its own: one that throws doesn't
  stop the others.
- **Main thread only** (Unity's). A call from another thread is logged and ignored.
- **Safe outside a session**: no-ops, empty lists and fallbacks (`Players` is empty, `Spawn` returns null, `Get`
  returns your fallback, `RunsThisScene` is true: you run your own game).

## Setup

Reference the multiplayer mod's DLLs **without copying them** (players get them from its own install):

```xml
<Reference Include="path\to\EmbervilleMP.dll" Private="false" />
<Reference Include="path\to\LiteNetLib.dll" Private="false" />  <!-- NetDataReader / NetDataWriter in the API -->
```

Its plugin GUID is `dev.emberville.mp`.

**Hard dependency** (your mod is multiplayer-only, like Party Tricks): BepInEx won't load you without it.

```csharp
[BepInPlugin("you.yourmod", "Your Mod", "1.0.0")]
[BepInDependency("dev.emberville.mp", BepInDependency.DependencyFlags.HardDependency)]
[MultiplayerMod(Requirement.Everyone)]
public class YourPlugin : BaseUnityPlugin { ... use Multiplayer.* anywhere ... }
```

**Soft dependency** (your mod also works alone): BepInEx loads you either way, so you must not touch the API's types
when the multiplayer mod isn't there. Ask BepInEx first, and keep every use of the API in a class of its own: the
runtime only loads that class (and fails to find EmbervilleMP.dll) when a method of it runs.

```csharp
[BepInDependency("dev.emberville.mp", BepInDependency.DependencyFlags.SoftDependency)]
[MultiplayerMod(Requirement.ClientOnly)]   // (only read by the multiplayer mod: harmless without it)
public class YourPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("dev.emberville.mp")) MpBridge.Init();
    }
}

internal static class MpBridge   // the only class that names EmbervilleMP.Api
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Init()
    {
        if (!Multiplayer.Available) return;
        Multiplayer.PlayerJoined += p => Multiplayer.ChatMessage(p.Name + " joined");
    }
}
```

Register channels, object kinds and spawnables in your `Awake` (every copy must have them before a session starts).

## Compatibility

Put `[MultiplayerMod(...)]` on your plugin class. When someone joins, both sides' declared mods (GUID, name, version
from `[BepInPlugin]`) are compared:

| Requirement | Meaning | At join |
|---|---|---|
| `Everyone` | every player needs it, same version (it sends messages, shares objects or data) | missing on either side or another version: **refused**, with a message the joining player reads: *"The host uses Party Tricks 1.0.0: install it to join."*, *"The host uses Party Tricks 1.0.0, you have 1.1.0: use the same version to join."*, *"You use Party Tricks 1.0.0 and the host doesn't: turn it off to join (or the host installs it)."* |
| `HostOnly` | only the host needs it (it changes the host's world) | never blocks |
| `ClientOnly` | only for the player who has it (visuals, UI) | never blocks |
| `Incompatible` | it breaks co-op | never blocks; turn your mod off while `Multiplayer.ShouldBeOff` is true (in a session) |

Mods **without** the attribute never block a join: they're listed as a warning in the log
(`[mods] X has no [MultiplayerMod] declaration`) and in the F8 window.

`Multiplayer.ModsOf(player)` lists a player's declared mods (`MpMod`: `Guid`, `Name`, `Version`, `Requirement`).

## The session and its players

```csharp
Multiplayer.Available      // the multiplayer mod is loaded
Multiplayer.InSession      // a session is up (we're hosting, or joined and welcomed)
Multiplayer.IsHost / IsGuest
Multiplayer.WorldName      // the session's world (the host's), "" outside a session
Multiplayer.TextFieldOpen  // our chat line or F8 window has the keyboard: skip your hotkeys

Multiplayer.Players        // everyone, us included, by id
Multiplayer.LocalPlayer
Multiplayer.GetPlayer(id)
Multiplayer.PlayerOf(gameObject)          // whose character or puppet this is (or a part of it), else null
Multiplayer.PlayersInMyScene()            // us included
Multiplayer.PlayersNear(position, radius) // in our scene, nearest first
```

`MpPlayer`: `Id`, `Name`, `IsLocal`, `IsHost`, `Scene` ("" between scenes), `Area` (the room), `IsDead`, `Position`,
`Character` (our character, or that player's puppet while it's shown here: same scene and room; else null). One
object per player and session: fine as a dictionary key until `PlayerLeft`.

```csharp
foreach (var p in Multiplayer.PlayersNear(myFire.transform.position, 3f))
    if (p.Character != null) ShowSparkOver(p.Character);
```

## Who decides

Players travel independently. Each scene is *run* by one copy: the host if it's there, else the lowest player id in
it. The runner simulates enemies and decides for the scene; the others show puppets.

```csharp
Multiplayer.RunsThisScene            // this copy runs the scene it's in (true outside a session)
Multiplayer.SceneRunner              // who does
Multiplayer.IsPuppet(gameObject)     // another player's puppet (or part of one)
Multiplayer.IsPuppetEnemy(enemy)     // an enemy shown from another copy (hits on it go to its runner)
```

Decide on the runner (or the host, for things that aren't a scene's), tell everyone. A runner can leave: the next
player takes over (`BecameSceneRunner`).

## Events

All on the main thread; each subscriber on its own.

| Event | When |
|---|---|
| `SessionStarted` / `SessionEnded` | we started hosting or were welcomed / the session ended for us |
| `PlayerJoined(p)` / `PlayerLeft(p)` | another player joined / left (a guest that just joined hears of those already there through `SessionStarted` and `Players`) |
| `PlayerDied(p)` / `PlayerRespawned(p)` | a player (us too) went down / got back up |
| `PlayerChangedScene(p)` | a player (us too) arrived in a scene |
| `BecameSceneRunner` / `StoppedRunningScene` | while in a session: this copy started / stopped running its scene |
| `WorldSwitching` | a guest is about to swap worlds (into the host's when joining, home when leaving) |

## Channels: your own messages

```csharp
static MpChannel ping;

void Awake()
{
    ping = Multiplayer.RegisterChannel("yourmod.ping", new ChannelOptions { MaxSize = 64, Rate = 5, Burst = 10 });
    ping.Received += (from, r) => ShowPing(from, new Vector2(r.GetFloat(), r.GetFloat()));
}

void Ping(Vector2 at) => ping.SendToAll(w => { w.Put(at.x); w.Put(at.y); });
```

- `SendToAll` (everyone else), `SendTo(player)`, `SendToHost()`, `SendToScene()` (everyone else in our scene), each
  with `reliable: true` (default) or `false` (may be lost, never late: positions, effects). Sending to yourself
  (`SendTo(LocalPlayer)`, `SendToHost` on the host) delivers here at once.
- `Received(from, reader)`: `from` is the verified sender. Every message goes through the host, which writes the
  sender's id itself: a guest can't pretend to be someone else.
- `ChannelOptions`: `MaxSize` (payload bytes, default 1024; a bigger message isn't sent), `Rate` / `Burst` (messages
  per second and at once, per sender: default 10 / 20), `WhoMaySend`: `Anyone`, `HostOnly`, `SceneRunnerOnly`. The
  host enforces them for every guest and drops what breaks them (a guest that keeps sending junk is kicked, as for
  the mod's own messages).
- A message on a channel the receiver doesn't have (a ClientOnly mod's) is counted, logged once and dropped; the host
  still passes it on to the players it's for, with default limits.
- Ids: unique, prefixed with your mod (`"yourmod.ping"`); `mp.*` is ours. On the wire a channel is a 4-byte hash of
  its id (two ids with the same hash are refused at registration).

## Shared data

Key/value data per mod: strings, ints, floats, bools.

```csharp
var world = Multiplayer.WorldStore("you.yourmod");     // saved with the host's world, sent to everyone who joins
var session = Multiplayer.SessionStore("you.yourmod"); // the same, forgotten when the session ends

world.Changed += key => Refresh(key);
if (Multiplayer.IsHost) world.Set("bells_rung", world.Get("bells_rung", 0) + 1);
int rung = world.Get("bells_rung", 0);
```

- `Set(key, value)` (string/int/float/bool), `Remove(key)`, `Get<T>(key, fallback)` (another type gives the fallback;
  an int reads as a float), `Has`, `Keys`, `Changed(key)` on every copy.
- The host's copy is the real one: a guest's `Set` goes to the host, which applies it and tells everyone (the guest
  too). On a guest the new value shows a round trip later; all copies see the same values in the same order.
- The world store is in `<world>_mp_mods.txt` next to the host's save, written whenever the game saves (a world side
  file: deleted with its world, its first line names the world). Keys: 64 characters, strings: 4096, 512 keys per mod.
- Outside a session `Set` does nothing and `Get` gives fallbacks.

## World objects

Objects placed in the scene that every player should see in the same state (a lamp, a puzzle piece): the same
framework as the multiplayer mod's own doors and chests.

```csharp
class LampKind : MpObjectHandler
{
    public override string Kind => "yourmod.lamp";
    public override IEnumerable<GameObject> FindAll() => Object.FindObjectsByType<Lamp>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(l => l.gameObject);
    public override void Capture(GameObject obj, NetDataWriter w) => w.Put(obj.GetComponent<Lamp>().On);
    public override void Apply(GameObject obj, NetDataReader r) => obj.GetComponent<Lamp>().SetOn(r.GetBool());
    public override void OnIntent(GameObject obj, MpPlayer from, NetDataReader r)   // on the scene's runner
    {
        obj.GetComponent<Lamp>().SetOn(!obj.GetComponent<Lamp>().On);
        Multiplayer.ObjectChanged(this, obj);
    }
}

static LampKind lamps = new LampKind();
void Awake() => Multiplayer.RegisterObjectKind(lamps);
void OnPlayerClicks(Lamp l) => Multiplayer.SendIntent(lamps, l.gameObject, w => { });   // the runner decides
```

- `ObjectChanged(kind, obj, byLocalPlayer = false)`: everyone gets its state. Sent when we run the scene, or when our
  player did it (`byLocalPlayer: true`). `Apply` must not call it back (it's ignored while applying anyway).
- `SendIntent(kind, obj, write)`: the runner's `OnIntent` decides (at once if that's us).
- Players arriving in the scene, a new runner, and the host (for scenes it isn't in, and next session: in
  `<world>_mp_objects.txt`) get the states. `Persistent => false` for state that only lasts while the scene is loaded.
- Objects are told apart by their path in the scene (unique names!), or by net id for spawned objects.
- `Kind`: unique, prefixed with your mod; the multiplayer mod's own kinds are taken.

## Spawned objects

Objects your mod creates at runtime, one copy on every machine with one net id.

```csharp
void Awake() => Multiplayer.RegisterSpawnable("yourmod.totem", r =>
{
    var go = new GameObject("Totem");
    go.AddComponent<Totem>().Colour = r.GetInt();
    return go;                                   // its position is set after
}, state: totemKind, saved: true);

Multiplayer.Spawn("yourmod.totem", position, w => w.Put(colour));
Multiplayer.Despawn(totem);
int id = Multiplayer.NetIdOf(totem);             // the same on every copy, 0 for anything else
```

- `Spawn` on the scene's runner makes the object at once and returns it; elsewhere the runner is asked and `null` is
  returned (the object arrives a moment later). Every copy in that scene makes it; players arriving later get it
  (with its state). `Despawn` is the runner's too (asked if it isn't us). Nothing outside a session.
- `state`: an `MpObjectHandler` for its state (registered as an object kind too; `FindAll` needn't return spawned
  objects). `saved: true`: kept with the host's world (`<world>_mp_mods.txt`) and back next session; otherwise it ends
  with the session.
- Party Tricks' campfire is a complete example: spawned, saved, with a state the runner counts down, and an intent to
  put it out.

## Helpers

```csharp
Multiplayer.ChatMessage("Only you see this");
Multiplayer.Announce("Everyone sees this");      // in everyone's chat, ours too
Multiplayer.Notify(player, "Only they see this");
Multiplayer.InCombat(player);                    // an enemy fighting near them (another player: as our copy knows it)
Multiplayer.TeleportTo(player, out string why);  // the teammate teleport: host's rule, out of combat, a cooldown
```

## Testing your mod

The multiplayer mod has a self-test runner built in: a scenario is a coroutine that every copy of the game in a test
session runs at the same time, with checks and barriers between the copies. In your mod (any static class, a public
static method returning `IEnumerator`):

```csharp
using T = EmbervilleMP.Api.Multiplayer.Testing;

[MpScenario("yourmod-lamps", Timeout = 150)]
public static IEnumerator LampsTest()
{
    yield return T.Ready();                    // in game, everyone in the session and the host's world, a barrier
    if (Multiplayer.IsHost) { ...; T.Tell("lamp", "on"); }
    else
    {
        yield return T.WaitFor(() => T.Heard("lamp") != null, 10f);
        T.Check("the host's lamp is on here", ...);
    }
    yield return T.Barrier("done");
    yield return T.Finish("lamps");
}
```

- `[MpScenario]` takes `Timeout` (seconds), `Players`, `Start` (`"own-world"`: the guests join during it, a late
  join), `Scene`, `Switches`, `Quick`, `Fresh`, `Covers`, `Isolate`. The multiplayer mod's own test suite uses them to
  plan its runs; they don't matter when you run a scenario yourself.
- `Multiplayer.Testing`: `Active`, `Current`, `Check`, `Barrier`, `WaitFor`, `WaitInGame`, `Ready`, `Tell` / `Heard`
  (key=value between the copies), `Finish`, `ChatHas`, `MoveLocalPlayer`, `SaveAndReloadWorld` (host: the game's save,
  then the mods' world data read back as a new session would). Test only: nothing there is for play.
- **Running one**: start the game twice on one PC (`Emberville.exe` with these launch options), each with its own save
  profile, so your real saves are never touched:
  ```
  host:  --mp-host=27115 --mp-profile=testhost --mp-name=Hosty --mp-test=yourmod-lamps --mp-autostart=TutorialArea --mp-quit=200
  guest: --mp-join=127.0.0.1:27115 --mp-profile=testguest --mp-name=Guesty --mp-test=yourmod-lamps --mp-autostart=TutorialArea --mp-quit=200
  ```
  Each copy writes `[MP] CHECK ok|FAIL ...` lines and a final `[MP] RESULT PASS|FAIL yourmod-lamps` to the BepInEx
  log. The multiplayer mod's own test suite runs the Party Tricks scenarios this way with every change.
- Both copies run from one game folder, so they have the same mods. `--mp-hide-mod=<guid>[,<guid>]` makes a copy act
  as if it didn't have a mod (`--mp-guest-hide-mod=<guid>`: the guests only). Add
  `--mp-guest-hide-mod=quadslab.emberville.partytricks` to both copies above and the guest is refused with
  `The host uses Party Tricks 1.0.0: install it to join.`

## Releasing your mod

Your mod is its own download, never inside the multiplayer mod's. On Thunderstore-format sites (Hexium) declare the
dependency on the multiplayer package, `Quads_Lab-Ember_Multiplayer-<version>` (the first version with API version 1
or later), next to `BepInEx-BepInExPack-5.4.2305`.

[emberville-mods](https://github.com/HardHeadHackerHead/emberville-mods)' `scripts\package.ps1` is a ready-made packager: copy it if you like. Given a mod folder
with a `store.json` and a 256 x 256 `icon.png`, it makes the plain zip (unzip into `BepInEx\plugins\`) and the
mod-site package (manifest, icon, README, CHANGELOG, `plugins\<Name>\<Name>.dll`).

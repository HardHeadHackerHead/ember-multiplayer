namespace EmbervilleMP.Net
{
    internal enum Msg : byte
    {
        // session
        Hello = 1,        // guest -> host: name
        Welcome = 2,      // host -> guest: your id, everyone already here
        PlayerJoined = 3, // host -> all: id, name
        PlayerLeft = 4,   // host -> all: id
        Discover = 5,     // LAN broadcast probe / reply (unconnected)
        Refuse = 6,       // host -> guest: why it can't join (then the connection closes)
        WorldRequest = 7, // guest -> host: send me your world
        WorldSnapshot = 8, // host -> guest: world + map + estate (zipped JSON) and where the host is

        // stage 1: presence
        State = 10,       // unreliable, ~20 Hz: where a player is and what their animator is doing
        Look = 11,        // reliable, on change: which parts of the character are shown, colours
        Chat = 12,        // reliable: text line
        PlayerProjectile = 13, // a player's projectile, shown (harmless) to the others

        // stage 2: co-op combat (see Sync/EnemySync.cs)
        EnemyStates = 20, // host -> all, unreliable batches
        EnemyHit = 21,    // guest -> host only
        EnemyDied = 22,   // host -> all
        EnemyAttack = 23, // host -> one guest
        EnemyAttackStart = 24, // host -> all: an enemy starts an attack (guests replay it for the visuals)
        EnemySpawn = 25,  // host -> all: an enemy appeared at runtime (pools: boss spiderlings, waves)
        EnemyDamaged = 26, // host -> all: an enemy took damage (damage numbers for hits by others)
        EnemyWake = 27,   // guest -> host: our game woke a dormant enemy (a boss after its intro); wake yours
        EggSpawn = 28,    // host -> all: the boss laid an egg
        EggHit = 29,      // guest -> host
        EggBroken = 30,   // host -> all: an egg broke (0) or hatched (n spiderlings)
        EnemyHandoff = 31, // old scene runner -> new one: the fight as it is (dead, health, positions)

        // phase B: shared world progress (see Sync/WorldState.cs)
        WorldBool = 40,      // a story flag changed
        WorldListAdd = 41,   // lore read / recipe or blueprint learned
        WorldTinetia = 42,   // Tinetia's map unlocks (flags, OR-merged)
        DialogueVar = 43,    // a dialogue variable changed
        TimeSync = 44,       // host -> all: day and time of day
        QuestSnapshot = 45,  // quest states and objective progress (merged, never backwards)

        // phase C/D: world objects (see Sync/Objects/ObjectSync.cs)
        ObjState = 50,       // an object's new state (scene, kind, id, state)
        ObjSnapshot = 51,    // all known object states of a scene (for arrivals and new scene runners)
        ObjIntent = 52,      // a player asks the scene's runner to do something to an object

        // interactables (see Sync/Objects/TrapSync.cs)
        TrapClock = 60,      // host -> all: its clock, so traps fire at the same moments everywhere

        // loot (see Sync/LootRules.cs, Sync/WorldDrops.cs)
        LootRules = 65,      // host -> guest: loot mode, and (instanced) the chests this player already opened
        LootChestRecord = 66, // guest -> host (instanced): what I left in a chest I opened
        LootSpawn = 67,      // scene runner -> all (shared): world drops with network ids
        LootDropRequest = 68, // player -> scene runner (shared): spawn these drops (my dropped item, my gathering)
        LootPickup = 69,     // player -> runner: may I take drop #n; runner -> all: who got it

        // phase D: the estate (see Sync/EstateRules.cs, Sync/EstateSync.cs)
        EstateRules = 70,    // host -> guest: estate unlocked, keep playing after the demo's end
        EstateOps = 71,      // estate changes: placeables, tiles, soil states (any -> all)
        EstateClaim = 72,    // player -> the estate scene's runner: may I harvest this soil
        EstateGrant = 73,    // runner -> all: who harvests that soil (or nobody)
        // story cutscenes (see Sync/Objects/CutsceneSync.cs)
        CutscenePlay = 75,   // a player set off a cutscene step in a room: everyone in that room plays it

        Voice = 80,          // a piece of a player's voice (Steam-compressed)

        Fishing = 90,        // a player's fishing phase and direction (see Sync/FishingSync.cs)
        // status effects (see Sync/StatusFx.cs)
        PlayerEffects = 81,  // a player's active effects and buffs (type, stacks, time left, web level), on change
        // world map (see Sync/MapSync.cs)
        MapOps = 85,         // explored cells, unlocked portals, map markers (deltas; or the host's whole map, on request)
        MapQuickPortal = 86, // quick portals: a guest asks the host for an id, the host places it for all; removals
        MapRequest = 87,     // guest -> host: send me your whole map (once I've loaded your world)
        // other players' skills and spells (see Sync/SkillFx.cs)
        SkillFxSpawn = 83,   // a player's attack or skill spawned an effect object: shown as a harmless copy
        SkillFxUpdate = 84,  // that object ended, was released (held beams) or moved
        DeathNotice = 92,    // a player died (and of what), got back up, or a boss fell (see Sync/Deaths.cs)
        // pings and teleporting to teammates (see Sync/Pings.cs, Sync/TeamTeleport.cs)
        Ping = 88,           // a player pinged a spot: scene, area, position
        CoopRules = 89,      // host -> guests: co-op options (teleport to teammates allowed)
        // party HUD (see Sync/PartyStatus.cs)
        PartyStatus = 78,    // a player's health, Kymia and level: on change (<= 5 Hz) and every 2 s
        // the world's stash and key items, the host's (see Sync/Objects/StashSync.cs, Sync/KeyItems.cs)
        StashAsk = 100,      // guest -> host: open / close a storage chest, what I moved in it, my copy of one I set up
        StashState = 101,    // host -> all: a storage chest's contents and who has it open
        StashTake = 102,     // guest -> host: what my crafting / repair took from shared chests
        KeyItemsChange = 103, // guest -> host: key items I got or used (since my last report)
        KeyItemsState = 104, // host -> guest: the world's key items (and the last report of yours they include)
        // teammates' presence (see Sync/TeamSounds.cs, Sync/TeamFx.cs)
        PlayerSounds = 120,  // sounds the game posted on a player's character (Wwise event ids), batched per frame
        PlayerMoment = 121,  // a player's one-off effect the state can't show: teleport / respawn flash, death mark
        PlayerFloatText = 122, // floating text over a player (damage taken, heals, Blocked...) and its health bar
        PlayerAnimSpeed = 123, // a player's animator speed (attacks with a speed multiplier), on change
        // the Spider Matriarch and the Acolyte Trackers (see Sync/EggSync.cs, Sync/BossSync.cs, Sync/TrackerSync.cs)
        EggStates = 115,     // scene runner -> all: the scene's placed eggs (active, idle / broken / hatched), on change and every few s
        MatriarchState = 116, // scene runner -> all: the boss's stage, so whoever runs the fight next carries on from it
        TrackerPull = 117,   // scene runner -> all: a tracker's hook caught this player (it pulls itself on its own copy)
        TrackerCamo = 118,   // scene runner -> all: which trackers of the scene are camouflaged (half see-through)
        // enemies' effects, visuals and hits beyond EnemySync (see Sync/EnemyFx.cs)
        EnemyEffects = 108,  // scene runner -> all, on change: enemies' active effects, invulnerability, tint
        EnemyVisual = 109,   // scene runner -> all: an effect popup, a graph's sprite fade, a group's spawn VFX
        EnemyEffectIntent = 110, // player -> runner: an effect our character put on a puppet (or took off it)
        EnemyFxHandoff = 111, // old runner -> new runner: active effects and boss stages
        EnemyHitRaw = 112,   // player -> runner: a hit before the target's side of CombatSystem (the runner resolves it)
        // story and world scripts (see Sync/CartographerSync.cs, Sync/WorldClock.cs, Sync/CharacterRewards.cs)
        CartographerBuy = 125,    // guest -> host: may I buy this Tinetia unlock (the host's world decides who pays)
        CartographerResult = 126, // host -> that guest: bought (pay now), or someone else was first (nothing to pay)
        TimeSet = 127,       // guest -> host: a script of ours set the time of day (the host's clock is everyone's)
        CharReward = 128,    // a character reward outside quests (items, classes, XP): every player gets it once
        // replayed enemy attacks (see Sync/EnemySync.cs, Sync/EnemyAim.cs)
        EnemyAttackCancel = 130, // scene runner -> all: an enemy's attack was interrupted (stagger, stun, death)
        EnemyAttackAim = 131,    // scene runner -> all: which moment of its target player the attack aimed at (that player's clock)
        // the fight beyond one runner (see Sync/EnemySync.Dead.cs, Sync/EnemyCombat.cs)
        EnemyDeadList = 135, // scene runner -> one arriving player: the scene's enemies dead in the runner's fight
        EnemyCombat = 136,   // scene runner -> all, on change + every 2 s: which enemies fight, and whom they chase
        // world objects' one-off reactions only the player's own copy saw (see Sync/WorldMoments.cs)
        WorldMoment = 155,   // a training dummy hit by a player, a recycler run
        // saves (see Sync/SaveSync.cs)
        SaveNow = 145,       // host -> guests: the host's world just reached the disk (items changed hands): save your character
        // party wipes and boss arenas (see Sync/PartyWipe.cs, Sync/BossArena.cs)
        SceneWipe = 140,     // scene runner -> all: everyone in the scene is down, it rewinds as each gets back up
        BossEvent = 141,     // scene runner -> all: a boss event (the Matriarch's roar, a screen shake) for everyone in its arena
        // other mods, through the public API (see Api/): all through the host, which checks and relays them
        ModMessage = 160,    // a mod's channel message: [target][arg][channel key][payload] (Api/ApiChannels.cs)
        ModState = 161,      // mods' shared stores and who has which mods (Api/ApiNet.cs, Api/ApiStores.cs)
        ModSpawn = 162,      // objects mods spawn at runtime, one net id everywhere (Api/ApiSpawns.cs)

    }

    internal static class Channels
    {
        public const byte Reliable = 0;
        public const byte State = 1;
        public const byte Count = 2;
    }

    internal static class Protocol
    {
        // Bump whenever a message layout changes; mismatched builds are refused at connect.
        public const int Version = 20;
        public const string Key = "EmbervilleMP";
        public const int MaxPlayers = 4;
        public const byte HostId = 1;
    }
}

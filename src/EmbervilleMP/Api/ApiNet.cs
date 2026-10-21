using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using LiteNetLib.Utils;

namespace EmbervilleMP.Api
{
    // The API on a session (built with the other syncs: MPRunner.Attach). Its messages:
    //   ModMessage: mods' channels (ApiChannels)
    //   ModState:   stores (ApiStores) and who has which mods:
    //       StoreSet (see ApiStores) | Hello: guest -> host, "send me everything" (asked again until answered)
    //       Dump: host -> guest: every player's mods, then every store value | Mods: host -> all, every player's mods
    //   ModSpawn:   spawned objects (ApiSpawns)
    // and it turns the session's state into the API's events (Multiplayer.*).
    internal class ApiNet
    {
        public const byte OpStoreSet = 1, OpHello = 2, OpDump = 3, OpMods = 4;

        public static ApiNet Instance { get; private set; }
        public static int DumpsSent, DumpsApplied;

        private readonly Session session;
        private bool dumped;
        private float helloAt = -100f;
        // every player's declared mods (the host has them from the handshake; guests from the host)
        private readonly Dictionary<byte, List<MpMod>> mods = new Dictionary<byte, List<MpMod>>();

        public ApiNet(Session s)
        {
            bool again = Instance != null && Instance.session == s;   // (tests: rebuilt on the same session after a world swap)
            Instance = this;
            session = s;
            ApiGuard.NoteMainThread();
            ApiMods.LogOnce();
            s.On(Msg.ModMessage, (from, r) => ApiChannels.OnMessage(s, from, r, s.Receiving));
            s.On(Msg.ModState, OnState);
            s.On(Msg.ModSpawn, (from, r) => ApiSpawns.OnMessage(s, from, r));
            if (s.IsHost)
            {
                // The world's mods' data: the file's, unless we have it in memory already (this world, newer than the file).
                if (ApiStores.WorldKey == null || ApiStores.WorldKey != QuestRewards.WorldKey) ApiSideFile.Load();
                mods[s.LocalId] = ApiMods.Local();
                s.PlayerJoined += p =>
                {
                    mods[p.Id] = p.Mods ?? new List<MpMod>();
                    SendMods();
                };
                s.PlayerLeft += p => mods.Remove(p.Id);
                foreach (var p in s.Players.Values) if (p.Id != s.LocalId) mods[p.Id] = p.Mods ?? new List<MpMod>();
                if (again) SendMods();
            }
            else
            {
                ApiStores.OnSessionEnded(wasHost: false);   // (anything from before is another world's)
                ApiSpawns.Forget();
            }
            ApiSpawns.Rescan();
            Multiplayer.OnAttached(s, again);
            s.Ended += () => OnEnded(s);
        }

        private void OnEnded(Session s)
        {
            if (Instance == this) Instance = null;
            ApiStores.OnSessionEnded(s.IsHost);
            ApiChannels.OnSessionEnded();
            if (s.IsHost) ApiSpawns.Unshow(); else ApiSpawns.Forget();
            Multiplayer.OnEnded(s);
        }

        public List<MpMod> ModsOf(byte id) => mods.TryGetValue(id, out var m) ? m : null;

        public void Update()
        {
            if (!session.Ready) return;
            // Host: its world loaded after the session started (hosting from the main menu), or another one: its data.
            if (session.IsHost && GameData.HasWorldData && !GuestWorld.Switching && ApiStores.WorldKey != QuestRewards.WorldKey)
            {
                ApiSideFile.Load();
                ApiSpawns.Rescan();
            }
            // Guest: everything the host has (mods, stores), asked until it answers.
            if (!session.IsHost && !dumped && UnityEngine.Time.realtimeSinceStartup - helloAt > 3f)
            {
                helloAt = UnityEngine.Time.realtimeSinceStartup;
                session.Send(Msg.ModState, w => w.Put(OpHello));
            }
            ApiSpawns.Update(session);
        }

        private void OnState(byte from, NetDataReader r)
        {
            byte op = r.GetByte();
            switch (op)
            {
                case OpStoreSet:
                    ApiStores.OnSet(session, from, r);
                    return;
                case OpHello:
                    if (!session.IsHost) return;
                    DumpsSent++;
                    session.SendTo(from, Msg.ModState, w => { w.Put(OpDump); WriteMods(w); ApiStores.WriteDump(w); });
                    return;
                case OpDump:
                    if (session.IsHost || from != Protocol.HostId) return;
                    ReadMods(r);
                    ApiStores.ReadDump(r);
                    dumped = true;
                    DumpsApplied++;
                    return;
                case OpMods:
                    if (session.IsHost || from != Protocol.HostId) return;
                    ReadMods(r);
                    return;
                default:
                    throw new System.FormatException("bad mod state op " + op);
            }
        }

        private void SendMods() => session.Send(Msg.ModState, w => { w.Put(OpMods); WriteMods(w); });

        private void WriteMods(NetDataWriter w)
        {
            w.Put((byte)mods.Count);
            foreach (var kv in mods) { w.Put(kv.Key); ApiMods.Write(w, kv.Value); }
        }

        private void ReadMods(NetDataReader r)
        {
            int n = NetGuard.Count(r.GetByte(), r, 2);
            mods.Clear();
            for (int i = 0; i < n; i++)
            {
                byte id = r.GetByte();
                mods[id] = ApiMods.Read(r);
            }
            mods[session.LocalId] = ApiMods.Local();
        }
    }
}

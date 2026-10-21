using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using LiteNetLib;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Stage 1: everyone sees everyone. Sends the local player's state and look, shows the others as puppets.
    internal class Presence
    {
        public const float StateRate = 20f;
        public const float LookCheckInterval = 0.25f;

        public readonly Dictionary<byte, RemotePlayer> Remotes = new Dictionary<byte, RemotePlayer>();
        public int StatesSent, StatesReceived, LooksSent, LooksReceived;

        private readonly Session session;
        private float nextState, nextLookCheck;
        private int lastLookHash;
        private bool forceLook = true;
        private List<int> floatParams;
        private PlayerMaster floatParamsFor;
        private readonly StateTrim trim = new StateTrim();

        // Tests: our look again (a fresh world rebuilt the others' Presence after ours: the one we sent went nowhere).
        public void SendLookAgain() => forceLook = true;

        public Presence(Session session)
        {
            this.session = session;
            session.On(Msg.State, (from, r) =>
            {
                StatesReceived++;
                var st = PlayerState.Read(r, from);
                if (st != null) Remote(from)?.OnState(st);
            });
            session.On(Msg.Look, (from, r) =>
            {
                LooksReceived++;
                Remote(from)?.OnLook(PlayerLook.Read(r));
            });
            session.PlayerJoined += p => { Remote(p.Id); forceLook = true; };
            session.PlayerLeft += p =>
            {
                if (Remotes.TryGetValue(p.Id, out var rp)) { rp.Destroy(); Remotes.Remove(p.Id); }
            };
            session.Welcomed += () => forceLook = true;
            session.Ended += Clear;
        }

        private RemotePlayer Remote(byte id)
        {
            if (id == session.LocalId) return null;
            if (!Remotes.TryGetValue(id, out var rp))
            {
                if (!session.Players.TryGetValue(id, out var info)) return null;
                rp = new RemotePlayer(info);
                Remotes[id] = rp;
            }
            return rp;
        }

        public void Update()
        {
            if (!session.Ready) return;
            var local = Game.LocalPlayer;
            float now = Time.realtimeSinceStartup;
            if (local != null && Game.InScene)   // (dead on our death screen we're still here: Game.InScene)
            {
                if (floatParamsFor != local)
                {
                    floatParams = local.animator.parameters.Where(p => p.type == AnimatorControllerParameterType.Float).Select(p => p.nameHash).ToList();
                    floatParamsFor = local;
                    forceLook = true;
                }
                if (now >= nextState)
                {
                    nextState = now + 1f / StateRate;
                    var s = PlayerState.Capture(local, floatParams);
                    TeamFx.Instance?.OnLocalState(s.Time, local);   // animator speed, on change
                    if (trim.Due(s, now))
                    {
                        session.Send(Msg.State, s.Write, DeliveryMethod.Sequenced);
                        StatesSent++;
                    }
                }
                if (now >= nextLookCheck)
                {
                    nextLookCheck = now + LookCheckInterval;
                    var graphics = local.transform.Find("Graphics");
                    if (graphics != null)
                    {
                        var look = PlayerLook.Capture(local.transform, graphics);
                        int hash = look.Hash();
                        if (forceLook || hash != lastLookHash)
                        {
                            session.Send(Msg.Look, look.Write);
                            lastLookHash = hash;
                            forceLook = false;
                            LooksSent++;
                        }
                    }
                }
            }
            else if (now >= nextState)
            {
                // Between scenes: tell the others at once, so whoever runs the scene we left knows we're gone.
                nextState = now + 0.5f;
                trim.NamesChanged();
                session.Send(Msg.State, new PlayerState { Time = now, Scene = "", Area = "", Visible = false }.Write, DeliveryMethod.Sequenced);
            }
            foreach (var rp in Remotes.Values) rp.Update();
        }

        public void Clear()
        {
            foreach (var rp in Remotes.Values) rp.Destroy();
            Remotes.Clear();
        }

        // Name tags over the other players.
        public void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null || (PartyHud.Instance != null && PartyHud.Instance.DrawsNameTags)) return;   // the party HUD draws them
            foreach (var rp in Remotes.Values)
            {
                if (!rp.Shown) continue;
                Vector3 sp = cam.WorldToScreenPoint(rp.HeadPosition);
                if (sp.z < 0) continue;
                var rect = new Rect(sp.x - 100, Screen.height - sp.y - 12, 200, 24);
                bool talking = Steam.Voice.Instance != null && Steam.Voice.Instance.IsSpeaking(rp.Info.Id);
                MPStyle.Shadowed(rect, talking ? "((" + rp.Info.Name + "))" : rp.Info.Name, MPStyle.NameTag);
            }
        }
    }
}

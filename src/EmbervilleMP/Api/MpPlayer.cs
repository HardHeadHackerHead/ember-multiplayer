using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP.Api
{
    // A player in the session (Multiplayer.Players). One object per player and session: safe to keep as a dictionary
    // key until PlayerLeft. Everything is read live from the session.
    public sealed class MpPlayer
    {
        public byte Id { get; }
        public string Name { get; }
        public bool IsLocal { get; }
        public bool IsHost => Id == Protocol.HostId;

        internal MpPlayer(byte id, string name, bool local)
        {
            Id = id;
            Name = name ?? "";
            IsLocal = local;
        }

        private RemotePlayer Remote =>
            !IsLocal && MPRunner.Instance?.Presence != null && MPRunner.Instance.Presence.Remotes.TryGetValue(Id, out var rp) ? rp : null;

        // The scene it's in ("" between scenes or not heard from yet).
        public string Scene
        {
            get
            {
                // (a guest still in its own world, joining: not in any of the session's scenes yet)
                if (IsLocal) return Game.InScene && (Multiplayer.IsHost || GuestWorld.Active && !GuestWorld.Switching) ? Game.SceneName : "";
                var st = Remote?.Latest;
                return st == null || Remote.SecondsSinceState > SceneAuthority.PresenceTimeout ? "" : st.Scene ?? "";
            }
        }

        // The room (GameArea) it's in, "" if unknown.
        public string Area => IsLocal ? (Game.InScene ? Game.AreaName : "") : Remote?.Latest?.Area ?? "";

        public bool IsDead => IsLocal ? Game.LocalPlayer != null && Game.LocalPlayer.IsDead : Remote?.Latest != null && Remote.Latest.Dead;

        // Where it is: our character, or the position that player last reported from a scene (not the puppet: it
        // plays ~0.1 s behind, and just after an arrival it can still show the "between scenes" state at 0,0).
        public Vector2 Position
        {
            get
            {
                if (IsLocal) return Game.LocalPlayer != null ? (Vector2)Game.LocalPlayer.transform.position : Vector2.zero;
                var st = Remote?.Latest;
                if (st != null && !string.IsNullOrEmpty(st.Scene)) lastPosition = st.Position;
                return lastPosition;
            }
        }
        private Vector2 lastPosition;

        // The character to show things over: ours, or this player's puppet while it's shown here (same scene and
        // room); null otherwise.
        public GameObject Character
        {
            get
            {
                if (IsLocal) return Game.LocalPlayer != null ? Game.LocalPlayer.gameObject : null;
                var rp = Remote;
                return rp != null && rp.Shown ? rp.Puppet : null;
            }
        }

        public override string ToString() => $"{Name}#{Id}";
    }
}

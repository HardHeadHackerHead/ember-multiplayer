using System;

namespace EmbervilleMP.Api
{
    // How a mod behaves in a multiplayer session (see docs/API.md, "Compatibility").
    public enum Requirement : byte
    {
        Everyone = 0,      // every player needs it, same version: a join without it (or with another version) is refused
        HostOnly = 1,      // only the host needs it (it changes the host's world); never blocks a join
        ClientOnly = 2,    // only for the player who has it (visuals, UI); never blocks a join
        Incompatible = 3,  // breaks co-op: it is told to turn itself off while a session is up (Multiplayer.ShouldBeOff)
    }

    // On a BepInEx plugin class: [MultiplayerMod(Requirement.Everyone)]. The join handshake exchanges every player's
    // mods that carry it (GUID + version, from the [BepInPlugin] attribute).
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MultiplayerModAttribute : Attribute
    {
        public Requirement Requirement { get; }
        public MultiplayerModAttribute(Requirement requirement) { Requirement = requirement; }
    }

    // A mod a player has (Multiplayer.ModsOf).
    public sealed class MpMod
    {
        public string Guid { get; }
        public string Name { get; }
        public string Version { get; }
        public Requirement Requirement { get; }

        public MpMod(string guid, string name, string version, Requirement requirement)
        {
            Guid = guid ?? "";
            Name = string.IsNullOrEmpty(name) ? Guid : name;
            Version = version ?? "";
            Requirement = requirement;
        }

        public override string ToString() => $"{Name} {Version}";
    }
}

using Mono.Cecil;

namespace EaCheck;

// --key value [value...] command-line options.
sealed class Opts
{
    private readonly Dictionary<string, List<string>> map = new(StringComparer.OrdinalIgnoreCase);

    public static Opts Parse(IEnumerable<string> args)
    {
        var o = new Opts();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--")) { key = a[2..]; if (!o.map.ContainsKey(key)) o.map[key] = new(); }
            else if (key != null) o.map[key].Add(a);
            else throw new ArgumentException("unexpected argument " + a);
        }
        return o;
    }

    public string? Get(string k) => map.TryGetValue(k, out var v) && v.Count > 0 ? v[^1] : null;
    public string Req(string k) => Get(k) ?? throw new ArgumentException("missing --" + k);
    public List<string> All(string k) => map.TryGetValue(k, out var v) ? v : new();
}

static class Names
{
    private static readonly Dictionary<string, string> Prim = new()
    {
        ["System.Void"] = "void", ["System.Boolean"] = "bool", ["System.Int32"] = "int", ["System.Single"] = "float",
        ["System.String"] = "string", ["System.Object"] = "object", ["System.Int64"] = "long", ["System.Double"] = "double",
        ["System.Byte"] = "byte", ["System.UInt32"] = "uint", ["System.Int16"] = "short", ["System.UInt16"] = "ushort",
        ["System.UInt64"] = "ulong", ["System.Char"] = "char", ["System.SByte"] = "sbyte",
    };

    public static string Strip(string n) { int i = n.IndexOf('`'); return i < 0 ? n : n[..i]; }

    public static TypeReference Element(TypeReference t)
    {
        while (t is TypeSpecification s) t = s.ElementType;
        return t;
    }

    // The assembly a type reference points into (outermost declaring type's scope).
    public static string? Scope(TypeReference t)
    {
        t = Element(t);
        if (t is GenericParameter) return null;
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition m => m.Assembly.Name.Name,
            _ => null,
        };
    }

    // Short readable type: GateDoor, List<int>, DamageData&, State<Enemy>.
    public static string Pretty(TypeReference t)
    {
        switch (t)
        {
            case GenericInstanceType g: return Strip(g.ElementType.Name) + "<" + string.Join(", ", g.GenericArguments.Select(Pretty)) + ">";
            case ByReferenceType b: return Pretty(b.ElementType) + "&";
            case ArrayType a: return Pretty(a.ElementType) + "[]";
            case PointerType p: return Pretty(p.ElementType) + "*";
            case TypeSpecification s: return Pretty(s.ElementType);
            case GenericParameter gp: return gp.Name;
        }
        if (Prim.TryGetValue(t.FullName, out var p2)) return p2;
        var name = Strip(t.Name);
        return t.DeclaringType != null ? Pretty(t.DeclaringType) + "." + name : name;
    }

    // Namespace-qualified readable type name: Namespace.Outer.Inner<...>.
    public static string Full(TypeReference t)
    {
        if (t is GenericInstanceType g) return Base(g.ElementType) + "<" + string.Join(", ", g.GenericArguments.Select(Pretty)) + ">";
        var e = Element(t);
        if (e is GenericParameter gp) return gp.Name;
        int tick = e.Name.IndexOf('`');
        return Base(e) + (tick >= 0 && int.TryParse(e.Name[(tick + 1)..], out int n) ? "<" + new string(',', n - 1) + ">" : "");
    }

    private static string Base(TypeReference e)
    {
        string prefix = e.DeclaringType != null ? Base(e.DeclaringType) + "." : (string.IsNullOrEmpty(e.Namespace) ? "" : e.Namespace + ".");
        return prefix + Strip(e.Name);
    }

    public static string Method(MethodReference m)
    {
        string gen = m.HasGenericParameters ? "<" + string.Join(", ", m.GenericParameters.Select(g => g.Name)) + ">" : "";
        string ret = m.ReturnType.FullName == "System.Void" ? "" : " : " + Pretty(m.ReturnType);
        return $"{Full(m.DeclaringType)}.{m.Name}{gen}({string.Join(", ", m.Parameters.Select(p => Pretty(p.ParameterType)))}){ret}";
    }

    public static string Params(MethodReference m) => string.Join(", ", m.Parameters.Select(p => Pretty(p.ParameterType) + " " + p.Name));

    public static string ParamSig(MethodReference m) => string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName));
}

static class Features
{
    public static bool TestOnly(string file) => file.StartsWith("Dev/", StringComparison.OrdinalIgnoreCase);

    // Our source file -> the feature a broken dependency there costs.
    public static string Of(string file)
    {
        string f = file.Replace('\\', '/');
        string n = Path.GetFileNameWithoutExtension(f);
        if (f.StartsWith("Dev/")) return "Self-tests & dev harness";
        if (f.StartsWith("Sync/Objects/"))
            return n switch
            {
                "DoorSync" => "Objects: doors",
                "InteractableSync" => "Objects: switches, plates, item locks, torches",
                "RoomEventSync" => "Objects: room events & enemy waves",
                "TrapSync" => "Objects: traps",
                "DestructibleSync" => "Objects: breakables",
                "RoomHiderSync" => "Objects: secret rooms",
                "ChestSync" => "Objects: chests",
                "StashSync" => "Objects: storage chests",
                "GatheringSync" => "Objects: gathering",
                "StoreSync" => "Objects: shops",
                "CraftingSync" or "StationsCommon" => "Objects: crafting stations",
                "RepairSync" => "Objects: repairs & portals",
                "SavedObjectSync" => "Objects: saved story objects",
                "NpcTalkSync" => "Objects: NPC talk lock",
                "ObjectSync" => "Objects: framework",
                _ => "Objects: " + n,
            };
        if (f.StartsWith("Patches/"))
            return n switch
            {
                "EnemyPatches" => "Combat: enemies",
                "DeathPatches" => "Co-op death",
                "SpeedPatches" => "Splash skip & loading",
                _ => "Patches: " + n,
            };
        if (f.StartsWith("Sync/"))
            return n switch
            {
                "EnemySync" or "HostWorld" or "RemotePlayerTarget" or "DamageCodec" => "Combat: enemies",
                "EggSync" => "Combat: boss eggs",
                "GuestWorld" or "Backups" => "Shared world & saves",
                "WorldState" => "Story, quests & time",
                "WorldDrops" or "LootRules" or "ItemCodec" => "Loot & drops",
                "KeyItems" => "Key items",
                "PlayerState" or "PlayerLook" or "RemotePlayer" or "PuppetBuilder" or "Presence" or "PlayerFx" => "Players (puppets)",
                "SceneAuthority" or "NetIds" => "Scene authority & ids",
                "Chat" => "Chat",
                _ => "Sync: " + n,
            };
        if (f.StartsWith("UI/")) return "Menus & UI";
        if (f.StartsWith("Steam/")) return "Steam & voice";
        if (f.StartsWith("Net/")) return "Networking";
        return "Core (" + n + ")";
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EmbervilleMP.Api
{
    // Shared key/value data of one mod (Multiplayer.WorldStore / SessionStore). The host's copy is the real one: a
    // guest's Set goes to the host, which applies it and tells everyone (the guest included), so every copy sees the
    // same values in the same order. Get on a guest shows the new value once the host's answer is back (a round trip).
    // A world store is saved with the host's world (<world>_mp_mods.txt) and sent to every player who joins; a session
    // store is the same but forgotten when the session ends. Outside a session: Set does nothing, Get gives fallbacks.
    public sealed class MpStore
    {
        public string ModId { get; }
        public bool SavedWithWorld { get; }
        public event Action<string> Changed;

        internal MpStore(string mod, bool world)
        {
            ModId = mod;
            SavedWithWorld = world;
        }

        private ApiStores.Table Table => ApiStores.TableOf(SavedWithWorld, ModId, create: false);

        public void Set(string key, string value) => ApiStores.Set(this, key, StoreValue.Of(value ?? ""));
        public void Set(string key, int value) => ApiStores.Set(this, key, StoreValue.Of(value));
        public void Set(string key, float value) => ApiStores.Set(this, key, StoreValue.Of(value));
        public void Set(string key, bool value) => ApiStores.Set(this, key, StoreValue.Of(value));
        public void Remove(string key) => ApiStores.Set(this, key, StoreValue.Removed);

        public bool Has(string key) => key != null && Table != null && Table.Values.ContainsKey(key);
        public IReadOnlyList<string> Keys => Table?.Values.Keys.ToList() ?? new List<string>();

        // The value as T (string, int, float, bool; an int reads as a float too), or fallback if there's none or it
        // is of another type.
        public T Get<T>(string key, T fallback = default)
        {
            if (key == null || Table == null || !Table.Values.TryGetValue(key, out var v)) return fallback;
            object o = v.Boxed;
            if (o is T t) return t;
            if (typeof(T) == typeof(float) && o is int i) return (T)(object)(float)i;
            if (typeof(T) == typeof(string)) return (T)(object)Convert.ToString(o, CultureInfo.InvariantCulture);
            return fallback;
        }

        internal void RaiseChanged(string key)
        {
            var h = Changed;
            if (h != null) ApiGuard.Raise(h, $"store {ModId} Changed({key})", d => ((Action<string>)d)(key));
        }

        public override string ToString() => (SavedWithWorld ? "world store " : "session store ") + ModId;
    }

    internal struct StoreValue
    {
        public const byte None = 0, Str = 1, Int = 2, Float = 3, Bool = 4;
        public byte Type;
        public string S;
        public int I;
        public float F;

        public static readonly StoreValue Removed = new StoreValue { Type = None };
        public static StoreValue Of(string s) => new StoreValue { Type = Str, S = s };
        public static StoreValue Of(int i) => new StoreValue { Type = Int, I = i };
        public static StoreValue Of(float f) => new StoreValue { Type = Float, F = f };
        public static StoreValue Of(bool b) => new StoreValue { Type = Bool, I = b ? 1 : 0 };

        public object Boxed => Type == Str ? S : Type == Int ? I : Type == Float ? F : Type == Bool ? (object)(I != 0) : null;

        public bool Same(StoreValue o) => Type == o.Type && (Type == Str ? S == o.S : Type == Float ? F.Equals(o.F) : I == o.I);

        public void Write(LiteNetLib.Utils.NetDataWriter w)
        {
            w.Put(Type);
            switch (Type)
            {
                case Str: w.Put(S ?? ""); break;
                case Int: case Bool: w.Put(I); break;
                case Float: w.Put(F); break;
            }
        }

        public static StoreValue Read(LiteNetLib.Utils.NetDataReader r)
        {
            var v = new StoreValue { Type = r.GetByte() };
            switch (v.Type)
            {
                case None: break;
                case Str: v.S = r.GetString(); break;
                case Int: v.I = r.GetInt(); break;
                case Bool: v.I = r.GetInt() != 0 ? 1 : 0; break;
                case Float:
                    v.F = r.GetFloat();
                    if (float.IsNaN(v.F) || float.IsInfinity(v.F)) throw new FormatException("bad float in a store value");
                    break;
                default: throw new FormatException("bad store value type " + v.Type);
            }
            return v;
        }

        // Side file: "<type>\t<value>" (strings base64: no tabs or line breaks in the file).
        public string ToText() => Type + "\t" + (Type == Str ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(S ?? ""))
            : Type == Float ? F.ToString("R", CultureInfo.InvariantCulture) : I.ToString(CultureInfo.InvariantCulture));

        public static StoreValue FromText(string type, string value)
        {
            byte t = byte.Parse(type, CultureInfo.InvariantCulture);
            switch (t)
            {
                case Str: return Of(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)));
                case Int: return Of(int.Parse(value, CultureInfo.InvariantCulture));
                case Bool: return Of(int.Parse(value, CultureInfo.InvariantCulture) != 0);
                case Float: return Of(float.Parse(value, CultureInfo.InvariantCulture));
                default: throw new FormatException("bad store value type " + t);
            }
        }
    }
}

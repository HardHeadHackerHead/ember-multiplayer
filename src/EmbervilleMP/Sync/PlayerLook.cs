using System.Collections.Generic;
using System.Text;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Rendering;

namespace EmbervilleMP.Sync
{
    // How a player looks: which parts of the character rig are switched on, their tints, and the
    // properties of the colour materials (hair, skin, clothes). Captured generically from the rig,
    // so hair styles, equipment and weapons all come along without knowing how the game picks them.
    internal class PlayerLook
    {
        public struct Node
        {
            public string Path;
            public bool Active;
            public bool HasRenderer;
            public bool RendererEnabled;
            public Color32 Color;
        }

        public struct MatProp
        {
            public string Name;
            public ShaderPropertyType Type;
            public Vector4 Value;
        }

        public readonly List<Node> Nodes = new List<Node>();
        public readonly Dictionary<string, List<MatProp>> Materials = new Dictionary<string, List<MatProp>>();

        // Materials that every player shares and that carry no per-player colour.
        public static bool IsSharedDefault(Material m) => m == null || m.name.StartsWith("Sprite-");

        public static PlayerLook Capture(Transform root, Transform graphics)
        {
            var look = new PlayerLook();
            var sb = new StringBuilder();
            void Walk(Transform t)
            {
                foreach (Transform child in t)
                {
                    var sr = child.GetComponent<SpriteRenderer>();
                    look.Nodes.Add(new Node
                    {
                        Path = PathOf(child, root, sb),
                        Active = child.gameObject.activeSelf,
                        HasRenderer = sr != null,
                        RendererEnabled = sr != null && sr.enabled,
                        Color = sr != null ? (Color32)sr.color : default,
                    });
                    if (sr != null && !IsSharedDefault(sr.sharedMaterial) && !look.Materials.ContainsKey(sr.sharedMaterial.name))
                        look.Materials[sr.sharedMaterial.name] = ReadMaterial(sr.sharedMaterial);
                    Walk(child);
                }
            }
            Walk(graphics);
            return look;
        }

        private static List<MatProp> ReadMaterial(Material m)
        {
            var props = new List<MatProp>();
            var shader = m.shader;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var type = shader.GetPropertyType(i);
                string name = shader.GetPropertyName(i);
                Vector4 v;
                switch (type)
                {
                    case ShaderPropertyType.Color: v = m.GetColor(name); break;
                    case ShaderPropertyType.Vector: v = m.GetVector(name); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range: v = new Vector4(m.GetFloat(name), 0, 0, 0); break;
                    case ShaderPropertyType.Int: v = new Vector4(m.GetInteger(name), 0, 0, 0); break;
                    default: continue; // textures are the same for everyone
                }
                props.Add(new MatProp { Name = name, Type = type, Value = v });
            }
            return props;
        }

        public static string PathOf(Transform t, Transform root, StringBuilder sb = null)
        {
            sb = sb ?? new StringBuilder();
            sb.Clear();
            var stack = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) stack.Add(x.name);
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                sb.Append(stack[i]);
                if (i > 0) sb.Append('/');
            }
            return sb.ToString();
        }

        // Cheap fingerprint so the look is only re-sent when something actually changed.
        public int Hash()
        {
            unchecked
            {
                int h = 17;
                foreach (var n in Nodes)
                    h = h * 31 + (n.Active ? 1 : 0) + (n.RendererEnabled ? 2 : 0) + (n.Color.r << 2) + (n.Color.g << 10) + (n.Color.b << 18) + (n.Color.a << 26);
                foreach (var kv in Materials)
                    foreach (var p in kv.Value)
                        h = h * 31 + p.Value.GetHashCode();
                return h;
            }
        }

        public void Write(NetDataWriter w)
        {
            w.Put(Nodes.Count);
            foreach (var n in Nodes)
            {
                w.Put(n.Path);
                w.Put((byte)((n.Active ? 1 : 0) | (n.HasRenderer ? 2 : 0) | (n.RendererEnabled ? 4 : 0)));
                if (n.HasRenderer) { w.Put(n.Color.r); w.Put(n.Color.g); w.Put(n.Color.b); w.Put(n.Color.a); }
            }
            w.Put((byte)Materials.Count);
            foreach (var kv in Materials)
            {
                w.Put(kv.Key);
                w.Put((byte)kv.Value.Count);
                foreach (var p in kv.Value)
                {
                    w.Put(p.Name);
                    w.Put((byte)p.Type);
                    w.Put(p.Value.x); w.Put(p.Value.y); w.Put(p.Value.z); w.Put(p.Value.w);
                }
            }
        }

        public static PlayerLook Read(NetDataReader r)
        {
            var look = new PlayerLook();
            int n = r.GetInt();
            for (int i = 0; i < n; i++)
            {
                var node = new Node { Path = r.GetString() };
                byte f = r.GetByte();
                node.Active = (f & 1) != 0;
                node.HasRenderer = (f & 2) != 0;
                node.RendererEnabled = (f & 4) != 0;
                if (node.HasRenderer) node.Color = new Color32(r.GetByte(), r.GetByte(), r.GetByte(), r.GetByte());
                look.Nodes.Add(node);
            }
            int mats = r.GetByte();
            for (int i = 0; i < mats; i++)
            {
                string name = r.GetString();
                int count = r.GetByte();
                var props = new List<MatProp>(count);
                for (int j = 0; j < count; j++)
                    props.Add(new MatProp
                    {
                        Name = r.GetString(),
                        Type = (ShaderPropertyType)r.GetByte(),
                        Value = new Vector4(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
                    });
                look.Materials[name] = props;
            }
            return look;
        }

        public static void ApplyMaterial(Material m, List<MatProp> props)
        {
            foreach (var p in props)
            {
                if (!m.HasProperty(p.Name)) continue;
                switch (p.Type)
                {
                    case ShaderPropertyType.Color: m.SetColor(p.Name, p.Value); break;
                    case ShaderPropertyType.Vector: m.SetVector(p.Name, p.Value); break;
                    case ShaderPropertyType.Int: m.SetInteger(p.Name, (int)p.Value.x); break;
                    default: m.SetFloat(p.Name, p.Value.x); break;
                }
            }
        }
    }
}

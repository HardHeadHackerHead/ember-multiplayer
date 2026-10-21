using Mono.Cecil;

namespace EaCheck;

enum MemberKind { Method, Field, Property, Getter, Setter, Event, Nested, Constructor }

// A member looked up by name at runtime (Harmony target, AccessTools / reflection string), as found in our IL.
sealed class LookupSpec
{
    public MemberKind Kind;
    public TypeReference? Type;          // null: not statically known (Traverse, obj.GetType())
    public string? Name;
    public List<TypeReference>? Args;    // null: any overload (ambiguity is an error)
    public bool DeclaredOnly;            // Harmony attributes, AccessTools.Declared*
    public bool ZeroParamFallback;       // AccessTools.Method: on ambiguity it takes the parameterless overload
    public string Api = "";              // how it's looked up, for the report

    public string Describe()
    {
        string type = Type == null ? "<dynamic>" : Names.Full(Type);
        string args = Args == null ? "" : "(" + string.Join(", ", Args.Select(Names.Pretty)) + ")";
        string kind = Kind switch
        {
            MemberKind.Getter => " (getter)",
            MemberKind.Setter => " (setter)",
            MemberKind.Field => " (field)",
            MemberKind.Property => " (property)",
            MemberKind.Event => " (event)",
            MemberKind.Nested => " (nested type)",
            _ => "",
        };
        return Kind == MemberKind.Constructor ? $"{type}.ctor{args}" : $"{type}.{Name}{args}{kind}";
    }

    public string Key => $"{Kind}|{Names.Scope(Type!)}|{Type?.FullName}::{Name}({(Args == null ? "*" : string.Join(",", Args.Select(a => a.FullName)))})|{DeclaredOnly}";
}

static class Lookup
{
    // Methods named `name` as reflection's GetMethod(name, AccessTools.all) sees them from `t`: declared ones plus
    // non-private inherited ones, a signature hidden by a more derived one counted once.
    private static List<MethodDefinition> Visible(GameBuild b, TypeDefinition t, string name)
    {
        var list = new List<MethodDefinition>();
        var sigs = new HashSet<string>();
        bool first = true;
        for (var cur = t; cur != null; cur = b.Base(cur), first = false)
        {
            foreach (var m in cur.Methods)
            {
                if (m.Name != name || m.IsConstructor) continue;
                if (!first && m.IsPrivate) continue;
                if (sigs.Add(Names.ParamSig(m))) list.Add(m);
            }
        }
        return list;
    }

    private static bool ArgsMatch(MethodDefinition m, List<TypeReference> args) =>
        m.Parameters.Count == args.Count && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(args.Select(a => a.FullName));

    // What the lookup finds in this build: (member, null) or (null, why not).
    public static (IMemberDefinition? found, string? error) Resolve(GameBuild b, LookupSpec s)
    {
        if (s.Type == null) return (null, "type not statically known");
        var td = b.Resolve(s.Type);
        if (td == null) return (null, $"type {Names.Full(s.Type)} is missing");
        string name = s.Name ?? "";
        switch (s.Kind)
        {
            case MemberKind.Method:
            {
                if (s.DeclaredOnly)
                {
                    var declared = td.Methods.Where(m => m.Name == name && !m.IsConstructor).ToList();
                    if (s.Args != null) declared = declared.Where(m => ArgsMatch(m, s.Args)).ToList();
                    if (declared.Count == 1) return (declared[0], null);
                    if (declared.Count == 0) return (null, "no such method declared on " + Names.Full(s.Type) + Candidates(b, td, name));
                    return (null, $"ambiguous: {declared.Count} overloads ({string.Join("; ", declared.Select(Names.Method))}); give argument types");
                }
                for (var cur = td; cur != null; cur = b.Base(cur))
                {
                    var found = Visible(b, cur, name);
                    if (s.Args != null) found = found.Where(m => ArgsMatch(m, s.Args)).ToList();
                    if (found.Count == 1) return (found[0], null);
                    if (found.Count > 1)
                    {
                        if (s.ZeroParamFallback)
                        {
                            var zero = found.FirstOrDefault(m => m.Parameters.Count == 0);
                            if (zero != null) return (zero, null);
                        }
                        return (null, $"ambiguous: {found.Count} overloads ({string.Join("; ", found.Select(Names.Method))}); give argument types");
                    }
                }
                return (null, "no such method" + Candidates(b, td, name));
            }
            case MemberKind.Constructor:
            {
                var ctors = td.Methods.Where(m => m.IsConstructor && !m.IsStatic).ToList();
                if (s.Args != null) ctors = ctors.Where(m => ArgsMatch(m, s.Args)).ToList();
                return ctors.Count >= 1 ? (ctors[0], null) : (null, "no such constructor");
            }
            case MemberKind.Field:
            {
                for (var cur = td; cur != null; cur = s.DeclaredOnly ? null : b.Base(cur))
                {
                    var f = cur.Fields.FirstOrDefault(x => x.Name == name);
                    if (f != null) return (f, null);
                }
                return (null, "no such field" + Candidates(b, td, name));
            }
            case MemberKind.Property:
            case MemberKind.Getter:
            case MemberKind.Setter:
            {
                for (var cur = td; cur != null; cur = s.DeclaredOnly ? null : b.Base(cur))
                {
                    var props = cur.Properties.Where(x => x.Name == name).ToList();
                    if (props.Count == 0) continue;
                    if (props.Count > 1) return (null, $"ambiguous: {props.Count} properties named {name}");
                    var p = props[0];
                    if (s.Kind == MemberKind.Getter) return p.GetMethod != null ? (p.GetMethod, null) : (null, "property has no getter");
                    if (s.Kind == MemberKind.Setter) return p.SetMethod != null ? (p.SetMethod, null) : (null, "property has no setter");
                    return (p, null);
                }
                return (null, "no such property" + Candidates(b, td, name));
            }
            case MemberKind.Event:
            {
                for (var cur = td; cur != null; cur = s.DeclaredOnly ? null : b.Base(cur))
                {
                    var e = cur.Events.FirstOrDefault(x => x.Name == name);
                    if (e != null) return (e, null);
                }
                return (null, "no such event");
            }
            case MemberKind.Nested:
            {
                var n = td.NestedTypes.FirstOrDefault(x => x.Name == name);
                return n != null ? (n, null) : (null, "no such nested type");
            }
        }
        return (null, "unsupported lookup");
    }

    // " (did you mean: ...)" from members with a similar name.
    public static string Candidates(GameBuild b, TypeDefinition td, string name)
    {
        var names = new List<string>();
        for (var cur = td; cur != null && names.Count < 6; cur = b.Base(cur))
        {
            if (!b.GameAssemblies.Contains(cur.Module.Assembly.Name.Name)) break;
            foreach (var m in cur.Methods)
                if (Similar(m.Name, name)) names.Add(Names.Method(m));
            foreach (var f in cur.Fields)
                if (Similar(f.Name, name)) names.Add($"field {Names.Pretty(f.FieldType)} {Names.Full(cur)}.{f.Name}");
            foreach (var p in cur.Properties)
                if (Similar(p.Name, name)) names.Add($"property {Names.Pretty(p.PropertyType)} {Names.Full(cur)}.{p.Name}");
        }
        names = names.Distinct().Take(6).ToList();
        return names.Count == 0 ? "" : "; this build has: " + string.Join("; ", names);
    }

    private static bool Similar(string a, string b)
    {
        if (a.StartsWith("<")) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        string x = a.ToLowerInvariant().TrimStart('_'), y = b.ToLowerInvariant().TrimStart('_');
        if (x.StartsWith("get_") || x.StartsWith("set_")) x = x[4..];
        if (y.StartsWith("get_") || y.StartsWith("set_")) y = y[4..];
        if (x.Length < 3 || y.Length < 3) return x == y;
        return x == y || x.Contains(y) || y.Contains(x);
    }

    // Does this method body use the member (call / field access / ldtoken)?
    public static bool BodyUses(MethodDefinition m, IMemberDefinition member)
    {
        if (!m.HasBody) return false;
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.Operand is MemberReference r && r.DeclaringType != null && r.Name == member.Name &&
                Names.Element(r.DeclaringType).FullName == member.DeclaringType.FullName)
                return true;
        }
        return false;
    }
}

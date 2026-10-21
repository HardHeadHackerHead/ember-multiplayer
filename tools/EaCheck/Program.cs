using Mono.Cecil;
using Mono.Cecil.Cil;

namespace EaCheck;

static class Program
{
    private static void Usage()
    {
        Console.WriteLine(@"EaCheck: EmbervilleMP's game dependencies (normally run through scripts\ea-check.ps1)

  run    --plugin <EmbervilleMP.dll> --src <src\EmbervilleMP> --baseline <demo game dir> --out <dir>
         [--target <other game dir>] [--hints <ea-hints.txt>] [--refs <dir with 0Harmony.dll> ...]
         Writes <out>\inventory.md (everything we depend on, by feature/file) and <out>\check.md (what's missing
         or changed in --target; without --target the baseline checks itself). Exit 0 = no problems, 1 = problems.

  mutate --in <Assembly-CSharp.dll copy> --out <dll> --op <op> [--op <op> ...]
         Makes a deliberately broken copy (for proving the checker). Ops:
           rename-method:Type::Name=NewName     rename-field:Type::name=newName      rename-type:Type=NewName
           rename-param:Type::Method:param=new  add-overload:Type::Method            enum:EnumType.Member=Value
           swap-call:Type::Method:CalleeType::Callee=NewCallee   (same declaring type and parameters)
           param-default:Type::Method:param=NewDefault          add-interface-method:Interface::Name

  rename-scene --in <globalgamemanagers copy> --out <file> --from <Scene> --to <Scene of the same length>");
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 2; }
            var o = Opts.Parse(args.Skip(1));
            return args[0] switch
            {
                "run" => Run(o),
                "mutate" => Mutate(o),
                "rename-scene" => RenameScene(o),
                _ => Fail(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("EaCheck error: " + e);
            return 2;
        }

        static int Fail() { Usage(); return 2; }
    }

    private static int Run(Opts o)
    {
        var baseline = GameBuild.Open(o.Req("baseline"));
        var target = o.Get("target") is string t ? GameBuild.Open(t) : baseline;
        string outDir = Path.GetFullPath(o.Req("out"));
        var inv = Scanner.Scan(o.Req("plugin"), o.Req("src"), baseline, o.Get("hints"), o.All("refs"));

        var findings = new List<Finding>();
        foreach (var d in inv.Deps.Values)
            foreach (var p in d.Check(target, baseline))
                findings.Add(new Finding(d, p));

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "inventory.md"), Report.Inventory(inv, baseline));
        File.WriteAllText(Path.Combine(outDir, "check.md"), Report.Check(inv, findings, target, baseline));

        var cats = inv.Deps.Values.GroupBy(d => d.Category).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine($"Dependencies: {inv.Deps.Count} ({string.Join(", ", cats)})");
        var problems = findings.Where(f => f.Problem.Severity != Severity.Info)
            .OrderBy(f => f.Problem.Severity).ThenBy(f => f.TestOnly).ThenBy(f => f.Features).ToList();
        string against = ReferenceEquals(target, baseline) ? "the baseline itself" : target.Root;
        if (problems.Count == 0) Console.WriteLine($"No problems against {against}.");
        else
        {
            Console.WriteLine($"{problems.Count} problems against {against}:");
            foreach (var p in problems.Take(40))
                Console.WriteLine($"  {p.Problem.Severity,-8} [{p.Features}]{(p.TestOnly ? " (test-only)" : "")} {p.Problem.Title}");
            if (problems.Count > 40) Console.WriteLine($"  ... and {problems.Count - 40} more (see check.md)");
        }
        int infos = findings.Count - problems.Count;
        if (infos > 0) Console.WriteLine($"{infos} notes (see check.md).");
        foreach (var w in inv.Warnings) Console.WriteLine("warning: " + w);
        Console.WriteLine("Inventory: " + Path.Combine(outDir, "inventory.md"));
        Console.WriteLine("Report:    " + Path.Combine(outDir, "check.md"));
        return problems.Count == 0 ? 0 : 1;
    }

    // ---------------- deliberately broken copies, to prove the checker catches breaks ----------------

    private static int Mutate(Opts o)
    {
        string input = Path.GetFullPath(o.Req("in")), output = Path.GetFullPath(o.Req("out"));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("--out must differ from --in");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(input)!);
        var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate });

        TypeDefinition Type(string name) => module.GetType(name) ?? throw new ArgumentException("no type " + name);
        (TypeDefinition, string) Split(string s)
        {
            int i = s.IndexOf("::", StringComparison.Ordinal);
            return (Type(s[..i]), s[(i + 2)..]);
        }

        foreach (var op in o.All("op"))
        {
            int colon = op.IndexOf(':');
            string kind = op[..colon], rest = op[(colon + 1)..];
            switch (kind)
            {
                case "rename-method":
                {
                    var (t, r) = Split(rest);
                    var (from, to) = (r.Split('=')[0], r.Split('=')[1]);
                    var ms = t.Methods.Where(m => m.Name == from).ToList();
                    if (ms.Count == 0) throw new ArgumentException("no method " + rest);
                    foreach (var m in ms) m.Name = to;
                    break;
                }
                case "rename-field":
                {
                    var (t, r) = Split(rest);
                    var f = t.Fields.First(x => x.Name == r.Split('=')[0]);
                    f.Name = r.Split('=')[1];
                    break;
                }
                case "rename-type":
                {
                    var t = Type(rest.Split('=')[0]);
                    t.Name = rest.Split('=')[1];
                    break;
                }
                case "rename-param":
                {
                    var (t, r) = Split(rest);
                    var parts = r.Split(':');
                    var (from, to) = (parts[1].Split('=')[0], parts[1].Split('=')[1]);
                    var ps = t.Methods.Where(m => m.Name == parts[0]).SelectMany(m => m.Parameters).Where(p => p.Name == from).ToList();
                    if (ps.Count == 0) throw new ArgumentException("no parameter " + rest);
                    foreach (var p in ps) p.Name = to;
                    break;
                }
                case "param-default":
                {
                    // Type::Method:param=new string default
                    var (t, r) = Split(rest);
                    var parts = r.Split(':');
                    var (pname, value) = (parts[1].Split('=')[0], parts[1].Split('=')[1]);
                    var ps = t.Methods.Where(m => m.Name == parts[0]).SelectMany(m => m.Parameters).Where(p => p.Name == pname && p.HasConstant).ToList();
                    if (ps.Count == 0) throw new ArgumentException("no optional parameter " + rest);
                    foreach (var p in ps) p.Constant = value;
                    break;
                }
                case "add-interface-method":
                {
                    var (t, name) = Split(rest);
                    if (!t.IsInterface) throw new ArgumentException(t.FullName + " isn't an interface");
                    t.Methods.Add(new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
                        MethodAttributes.NewSlot | MethodAttributes.HideBySig, module.TypeSystem.Void));
                    break;
                }
                case "add-overload":
                {
                    var (t, name) = Split(rest);
                    var src = t.Methods.First(m => m.Name == name);
                    var attrs = src.Attributes & ~(MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot | MethodAttributes.Final);
                    var clone = new MethodDefinition(src.Name, attrs, src.ReturnType);
                    foreach (var p in src.Parameters) clone.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
                    clone.Parameters.Add(new ParameterDefinition("extra", ParameterAttributes.None, module.TypeSystem.Int32));
                    var il = clone.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Throw);
                    t.Methods.Add(clone);
                    break;
                }
                case "enum":
                {
                    int eq = rest.IndexOf('='), dot = rest.LastIndexOf('.', eq);
                    var t = Type(rest[..dot]);
                    var f = t.Fields.First(x => x.IsStatic && x.Name == rest[(dot + 1)..eq]);
                    var underlying = t.Fields.First(x => x.Name == "value__").FieldType;
                    f.Constant = underlying.FullName switch
                    {
                        "System.Int32" => (object)int.Parse(rest[(eq + 1)..]),
                        "System.Int16" => short.Parse(rest[(eq + 1)..]),
                        "System.Byte" => byte.Parse(rest[(eq + 1)..]),
                        "System.Int64" => long.Parse(rest[(eq + 1)..]),
                        _ => throw new ArgumentException("enum underlying type " + underlying.FullName),
                    };
                    break;
                }
                case "swap-call":
                {
                    // Type::Method:CalleeType::Callee=NewCallee
                    var (t, r) = Split(rest);
                    int c = r.IndexOf(':');
                    var method = t.Methods.First(m => m.Name == r[..c] && m.HasBody);
                    var callee = r[(c + 1)..];
                    int sep = callee.IndexOf("::", StringComparison.Ordinal);
                    string calleeType = callee[..sep], calleeName = callee[(sep + 2)..].Split('=')[0], newName = callee.Split('=')[1];
                    int swapped = 0;
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.Operand is not MethodReference mr || mr.Name != calleeName || mr.DeclaringType.FullName != calleeType) continue;
                        var def = mr.DeclaringType.Resolve().Methods.First(m => m.Name == newName && Names.ParamSig(m) == Names.ParamSig(mr));
                        ins.Operand = module.ImportReference(def);
                        swapped++;
                    }
                    if (swapped == 0) throw new ArgumentException("no call to swap: " + rest);
                    break;
                }
                default: throw new ArgumentException("unknown op " + kind);
            }
            Console.WriteLine("mutated: " + op);
        }
        module.Write(output);
        Console.WriteLine("wrote " + output);
        return 0;
    }

    private static int RenameScene(Opts o)
    {
        string input = Path.GetFullPath(o.Req("in")), output = Path.GetFullPath(o.Req("out"));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("--out must differ from --in");
        string from = "/" + o.Req("from") + ".unity", to = "/" + o.Req("to") + ".unity";
        if (from.Length != to.Length) throw new ArgumentException("scene names must have the same length");
        var bytes = File.ReadAllBytes(input);
        var a = System.Text.Encoding.ASCII.GetBytes(from);
        var b = System.Text.Encoding.ASCII.GetBytes(to);
        int count = 0;
        for (int i = 0; i + a.Length <= bytes.Length; i++)
            if (bytes.AsSpan(i, a.Length).SequenceEqual(a)) { b.CopyTo(bytes, i); count++; }
        if (count == 0) throw new ArgumentException("scene not found: " + from);
        File.WriteAllBytes(output, bytes);
        Console.WriteLine($"renamed {count} occurrence(s) of {from} -> {to} in {output}");
        return 0;
    }
}

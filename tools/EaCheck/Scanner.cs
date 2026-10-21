using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace EaCheck;

sealed class Inventory
{
    public string PluginPath = "", SrcRoot = "";
    public ModuleDefinition Module = null!;
    public readonly Dictionary<string, Dep> Deps = new();
    public readonly List<string> Warnings = new();

    public T Add<T>(T dep, Use use) where T : Dep
    {
        if (Deps.TryGetValue(dep.Key, out var d)) { d.AddUse(use); return (T)d; }
        dep.AddUse(use);
        Deps[dep.Key] = dep;
        return dep;
    }
}

// Reads EmbervilleMP.dll (+ its pdb for file:line) and our sources, and lists every game dependency.
static class Scanner
{
    private enum Role { Target, Transpiler }

    private sealed record Api(MemberKind Kind, bool Declared, bool ZeroFallback, bool Dynamic);

    private static readonly Dictionary<(string, string), Api> Apis = new()
    {
        [("HarmonyLib.AccessTools", "Method")] = new(MemberKind.Method, false, true, false),
        [("HarmonyLib.AccessTools", "DeclaredMethod")] = new(MemberKind.Method, true, false, false),
        [("HarmonyLib.AccessTools", "Field")] = new(MemberKind.Field, false, false, false),
        [("HarmonyLib.AccessTools", "DeclaredField")] = new(MemberKind.Field, true, false, false),
        [("HarmonyLib.AccessTools", "Property")] = new(MemberKind.Property, false, false, false),
        [("HarmonyLib.AccessTools", "DeclaredProperty")] = new(MemberKind.Property, true, false, false),
        [("HarmonyLib.AccessTools", "PropertyGetter")] = new(MemberKind.Getter, false, false, false),
        [("HarmonyLib.AccessTools", "DeclaredPropertyGetter")] = new(MemberKind.Getter, true, false, false),
        [("HarmonyLib.AccessTools", "PropertySetter")] = new(MemberKind.Setter, false, false, false),
        [("HarmonyLib.AccessTools", "DeclaredPropertySetter")] = new(MemberKind.Setter, true, false, false),
        [("HarmonyLib.AccessTools", "FieldRefAccess")] = new(MemberKind.Field, false, false, false),
        [("HarmonyLib.AccessTools", "StaticFieldRefAccess")] = new(MemberKind.Field, false, false, false),
        [("HarmonyLib.AccessTools", "Inner")] = new(MemberKind.Nested, false, false, false),
        [("System.Type", "GetMethod")] = new(MemberKind.Method, false, false, false),
        [("System.Type", "GetField")] = new(MemberKind.Field, false, false, false),
        [("System.Type", "GetProperty")] = new(MemberKind.Property, false, false, false),
        [("System.Type", "GetEvent")] = new(MemberKind.Event, false, false, false),
        [("System.Type", "GetNestedType")] = new(MemberKind.Nested, false, false, false),
        [("HarmonyLib.Traverse", "Field")] = new(MemberKind.Field, false, false, true),
        [("HarmonyLib.Traverse", "Method")] = new(MemberKind.Method, false, false, true),
        [("HarmonyLib.Traverse", "Property")] = new(MemberKind.Property, false, false, true),
    };

    private static readonly HashSet<string> PatchMethodNames = new() { "Prefix", "Postfix", "Transpiler", "Finalizer", "ILManipulator" };
    private static readonly Dictionary<string, string> PatchMethodAttrs = new()
    {
        ["HarmonyLib.HarmonyPrefix"] = "Prefix", ["HarmonyLib.HarmonyPostfix"] = "Postfix",
        ["HarmonyLib.HarmonyTranspiler"] = "Transpiler", ["HarmonyLib.HarmonyFinalizer"] = "Finalizer",
    };

    public static Inventory Scan(string pluginPath, string srcRoot, GameBuild baseline, string? hintsPath, IEnumerable<string> refDirs)
    {
        var inv = new Inventory { PluginPath = Path.GetFullPath(pluginPath), SrcRoot = Path.GetFullPath(srcRoot) };
        string pdb = Path.ChangeExtension(pluginPath, ".pdb");
        // Our own references (0Harmony, BepInEx...) must resolve too: Cecil needs e.g. HarmonyLib.ArgumentType to read
        // [HarmonyPatch(..., new[] { ArgumentType.Out })] attribute blobs.
        var resolver = new DefaultAssemblyResolver();
        foreach (var d in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(d);
        foreach (var d in new[] { Path.GetDirectoryName(Path.GetFullPath(pluginPath))!, baseline.ManagedDir, Path.Combine(baseline.Root, "BepInEx", "core") }
                     .Concat(refDirs).Where(Directory.Exists))
            resolver.AddSearchDirectory(d);
        var module = ModuleDefinition.ReadModule(pluginPath, new ReaderParameters
        {
            ReadSymbols = File.Exists(pdb), InMemory = true, AssemblyResolver = resolver,
        });
        inv.Module = module;
        if (resolver.GetSearchDirectories().All(d => !File.Exists(Path.Combine(d, "0Harmony.dll"))))
            inv.Warnings.Add("0Harmony.dll not found (pass --refs <BepInEx\\core>): hooks given with ArgumentType arrays can't be read");
        if (!File.Exists(pdb)) inv.Warnings.Add("no EmbervilleMP.pdb next to the dll: no file:line information");
        var s = new State(inv, baseline);
        s.Run();
        ScanSources(inv, baseline);
        if (hintsPath != null && File.Exists(hintsPath)) ReadHints(inv, hintsPath);
        var hinted = inv.Deps.Values.OfType<HintDep>().Select(h => h.Member).ToHashSet();
        foreach (var d in inv.Deps.Values.OfType<DynamicDep>()) d.Covered = hinted.Contains(d.Name);
        return inv;
    }

    private sealed class State
    {
        private readonly Inventory inv;
        private readonly GameBuild baseline;
        private readonly Dictionary<TypeDefinition, string> typeFiles = new();
        private readonly Dictionary<MethodDefinition, (HarmonyPatchDep Dep, Role Role)> roles = new();

        public State(Inventory inv, GameBuild baseline) { this.inv = inv; this.baseline = baseline; }

        private string Rel(string url)
        {
            var u = url.Replace('\\', '/');
            int i = u.IndexOf("/src/EmbervilleMP/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return u[(i + "/src/EmbervilleMP/".Length)..];
            var root = inv.SrcRoot.Replace('\\', '/').TrimEnd('/') + "/";
            return u.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? u[root.Length..] : Path.GetFileName(u);
        }

        private static TypeDefinition Outer(TypeDefinition t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t;
        }

        // Readable name of our method, through compiler-generated lambdas/iterators: Class.Method.
        private static string OurName(MethodDefinition m)
        {
            string name = m.Name;
            var t = m.DeclaringType;
            var lambda = Regex.Match(name, @"^<([^>]+)>");
            if (lambda.Success) name = lambda.Groups[1].Value;
            while (t.Name.StartsWith("<") && t.DeclaringType != null)
            {
                var it = Regex.Match(t.Name, @"^<([^>]+)>d__");
                if (it.Success) name = it.Groups[1].Value;
                t = t.DeclaringType;
            }
            return Names.Strip(t.Name) + "." + name;
        }

        private string FileOf(TypeDefinition t) => typeFiles.TryGetValue(Outer(t), out var f) ? f : Names.Strip(Outer(t).Name) + ".cs?";

        private Use TypeUse(TypeDefinition t) => new(FileOf(t), 0, Names.Strip(t.Name));

        private Use UseAt(MethodDefinition m, Instruction? ins)
        {
            var sps = m.DebugInformation.HasSequencePoints ? m.DebugInformation.SequencePoints : null;
            if (sps != null)
            {
                SequencePoint? best = null;
                foreach (var sp in sps)
                {
                    if (sp.IsHidden) continue;
                    if (ins == null) { best = sp; break; }
                    if (sp.Offset <= ins.Offset) best = sp;
                    else if (best != null) break;
                }
                best ??= sps.FirstOrDefault(sp => !sp.IsHidden);
                if (best != null) return new Use(Rel(best.Document.Url), best.StartLine, OurName(m));
            }
            // iterator/async stubs have no lines: their state machine's MoveNext does
            var moveNext = ins == null ? StateMachine(m)?.Methods.FirstOrDefault(x => x.Name == "MoveNext") : null;
            if (moveNext != null && moveNext.DebugInformation.HasSequencePoints) return UseAt(moveNext, null);
            return new Use(FileOf(m.DeclaringType), 0, OurName(m));
        }

        public void Run()
        {
            var types = inv.Module.GetTypes().ToList();
            foreach (var t in types)
                foreach (var m in t.Methods)
                {
                    var sp = m.DebugInformation.HasSequencePoints ? m.DebugInformation.SequencePoints.FirstOrDefault(p => !p.IsHidden) : null;
                    if (sp != null && !typeFiles.ContainsKey(Outer(t))) typeFiles[Outer(t)] = Rel(sp.Document.Url);
                }
            foreach (var t in types) FindPatchClass(t);
            foreach (var t in types) ScanType(t);
        }

        // ---- Harmony patch classes ----

        private static TypeDefinition? StateMachine(MethodDefinition m)
        {
            var ca = m.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName is "System.Runtime.CompilerServices.IteratorStateMachineAttribute"
                or "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
            if (ca == null || ca.ConstructorArguments.Count == 0 || ca.ConstructorArguments[0].Value is not TypeReference tr) return null;
            return tr as TypeDefinition ?? tr.Resolve();
        }

        private void AddRole(MethodDefinition m, HarmonyPatchDep dep, Role role)
        {
            roles[m] = (dep, role);
            var sm = StateMachine(m);
            if (sm != null) foreach (var x in sm.Methods) roles[x] = (dep, role);
        }

        private void FindPatchClass(TypeDefinition t)
        {
            var attrs = t.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").ToList();
            if (attrs.Count == 0) return;
            var dep = new HarmonyPatchDep(t);
            TypeReference? declType = null; string? name = null; List<TypeReference>? args = null; int[]? variations = null; int? methodType = null;
            foreach (var a in attrs)
                foreach (var arg in a.ConstructorArguments)
                {
                    string tn = arg.Type.FullName;
                    if (tn == "System.Type") declType = (TypeReference)arg.Value;
                    else if (tn == "System.String") name = (string)arg.Value;
                    else if (tn == "HarmonyLib.MethodType") methodType = Convert.ToInt32(arg.Value);
                    else if (tn == "System.Type[]") args = ((CustomAttributeArgument[])arg.Value).Select(x => (TypeReference)x.Value).ToList();
                    else if (tn == "HarmonyLib.ArgumentType[]") variations = ((CustomAttributeArgument[])arg.Value).Select(x => Convert.ToInt32(x.Value)).ToArray();
                    else dep.Notes.Add("HarmonyPatch argument of type " + tn + " not analysed");
                }
            if (args != null && variations != null)
                for (int i = 0; i < args.Count && i < variations.Length; i++)
                    if (variations[i] is 1 or 2) args[i] = new ByReferenceType(args[i]); // Ref, Out

            bool hasTargetMethod = false;
            foreach (var m in t.Methods)
            {
                var attrNames = m.CustomAttributes.Select(a => a.AttributeType.FullName).ToList();
                if (m.Name is "TargetMethod" or "TargetMethods" || attrNames.Contains("HarmonyLib.HarmonyTargetMethod") || attrNames.Contains("HarmonyLib.HarmonyTargetMethods"))
                {
                    hasTargetMethod = true;
                    AddRole(m, dep, Role.Target);
                }
                string? kind = PatchMethodNames.Contains(m.Name) ? m.Name : attrNames.Select(n => PatchMethodAttrs.GetValueOrDefault(n)).FirstOrDefault(n => n != null);
                if (kind != null)
                {
                    dep.PatchMethods.Add(m);
                    if (kind == "Transpiler") AddRole(m, dep, Role.Transpiler);
                }
                if (attrNames.Contains("HarmonyLib.HarmonyPatch")) dep.Notes.Add($"method-level [HarmonyPatch] on {m.Name} not analysed");
            }

            if (!hasTargetMethod)
            {
                if (declType == null || (name == null && methodType is null or 0 or 1 or 2 or 5))
                    dep.Notes.Add("target not fully given by the attributes");
                else
                {
                    var spec = new LookupSpec { Type = declType, Name = name, Args = args, DeclaredOnly = true, Api = "[HarmonyPatch]" };
                    spec.Kind = methodType switch
                    {
                        1 => MemberKind.Getter,
                        2 => MemberKind.Setter,
                        3 => MemberKind.Constructor,
                        _ => MemberKind.Method,
                    };
                    dep.Targets.Add((spec, "[HarmonyPatch] attribute"));
                }
            }
            var first = dep.PatchMethods.FirstOrDefault() ?? t.Methods.FirstOrDefault();
            inv.Add(dep, first != null ? UseAt(first, null) : TypeUse(t));
            if (declType != null) AddType(declType, first != null ? UseAt(first, null) : TypeUse(t));
        }

        // ---- types, signatures, IL ----

        private void AddType(TypeReference? t, Use use)
        {
            switch (t)
            {
                case null: return;
                case GenericInstanceType g:
                    AddType(g.ElementType, use);
                    foreach (var a in g.GenericArguments) AddType(a, use);
                    return;
                case TypeSpecification s: AddType(s.ElementType, use); return;
                case GenericParameter: return;
                case TypeDefinition: return; // ours
            }
            if (baseline.IsGame(t)) inv.Add(new TypeDep(t), use);
        }

        private void ScanAttributes(ICustomAttributeProvider p, Use use)
        {
            if (!p.HasCustomAttributes) return;
            foreach (var ca in p.CustomAttributes)
            {
                AddType(ca.AttributeType, use);
                foreach (var arg in ca.ConstructorArguments.Concat(ca.Properties.Select(x => x.Argument)).Concat(ca.Fields.Select(x => x.Argument)))
                {
                    if (arg.Value is TypeReference tr) AddType(tr, use);
                    else if (arg.Value is CustomAttributeArgument[] arr) foreach (var x in arr) if (x.Value is TypeReference tr2) AddType(tr2, use);
                }
            }
        }

        private void ScanType(TypeDefinition t)
        {
            var tUse = TypeUse(t);
            ScanAttributes(t, tUse);
            if (t.BaseType != null)
            {
                AddType(t.BaseType, tUse);
                if (baseline.IsGame(t.BaseType))
                {
                    bool abstractChain = false;
                    for (var cur = baseline.Resolve(t.BaseType); cur != null && baseline.GameAssemblies.Contains(cur.Module.Assembly.Name.Name); cur = baseline.Base(cur))
                        abstractChain |= cur.IsAbstract;
                    if (abstractChain) inv.Add(new ImplementsDep(t, t.BaseType, false), tUse);
                }
            }
            foreach (var i in t.Interfaces)
            {
                AddType(i.InterfaceType, tUse);
                if (baseline.IsGame(i.InterfaceType)) inv.Add(new ImplementsDep(t, i.InterfaceType, true), tUse);
            }
            foreach (var f in t.Fields) { AddType(f.FieldType, tUse); ScanAttributes(f, tUse); }
            foreach (var p in t.Properties) { AddType(p.PropertyType, tUse); ScanAttributes(p, tUse); }
            foreach (var e in t.Events) AddType(e.EventType, tUse);
            foreach (var m in t.Methods) ScanMethod(m);
        }

        private void ScanMethod(MethodDefinition m)
        {
            var mUse = UseAt(m, null);
            ScanAttributes(m, mUse);
            AddType(m.ReturnType, mUse);
            foreach (var p in m.Parameters) { AddType(p.ParameterType, mUse); ScanAttributes(p, mUse); }
            foreach (var o in m.Overrides) AddType(o.DeclaringType, mUse);
            if (!m.HasBody) return;
            foreach (var v in m.Body.Variables) AddType(v.VariableType, mUse);
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.Operand == null) continue;
                Use? use = null;
                Use U() => use ??= UseAt(m, ins);
                switch (ins.Operand)
                {
                    case MethodReference mr:
                    {
                        var callee = mr is GenericInstanceMethod gim ? gim.ElementMethod : mr;
                        if (mr is GenericInstanceMethod g2) foreach (var a in g2.GenericArguments) AddType(a, U());
                        AddType(callee.DeclaringType, U());
                        if (callee is not MethodDefinition && baseline.IsGame(callee.DeclaringType)) inv.Add(new MethodDep(callee), U());
                        if (ins.OpCode.FlowControl == FlowControl.Call) TryLookup(m, ins, mr, U());
                        break;
                    }
                    case FieldReference fr:
                        AddType(fr.DeclaringType, U());
                        if (fr is not FieldDefinition && baseline.IsGame(fr.DeclaringType))
                            inv.Add(new FieldDep(fr, ins.OpCode.Code is Code.Ldsfld or Code.Stsfld or Code.Ldsflda), U());
                        break;
                    case TypeReference tr:
                        AddType(tr, U());
                        break;
                    case string str:
                        if (baseline.Scenes != null && baseline.Scenes.Contains(str)) inv.Add(new SceneDep(str), U());
                        break;
                }
            }
        }

        // ---- lookups by name (AccessTools, reflection, Traverse) ----

        private static bool IsGetTypeFromHandle(Instruction i) =>
            i.OpCode.FlowControl == FlowControl.Call && i.Operand is MethodReference r && r.Name == "GetTypeFromHandle" && r.DeclaringType.FullName == "System.Type";

        private void TryLookup(MethodDefinition caller, Instruction call, MethodReference mr, Use use)
        {
            var callee = mr is GenericInstanceMethod gim ? gim.ElementMethod : mr;
            if (!Apis.TryGetValue((callee.DeclaringType.FullName, callee.Name), out var api)) return;
            string apiName = $"{Names.Strip(callee.DeclaringType.Name)}.{callee.Name}";
            var spec = Parse(call, mr, api, apiName);
            roles.TryGetValue(caller, out var role);
            if (role.Dep != null && role.Role == Role.Target)
            {
                if (spec?.Type != null) role.Dep.Targets.Add((spec, "TargetMethod(s): " + apiName));
                else role.Dep.Notes.Add("a TargetMethod lookup isn't statically known");
                return;
            }
            if (role.Dep != null && role.Role == Role.Transpiler)
            {
                if (spec?.Type != null && baseline.IsGame(spec.Type)) role.Dep.Anchors.Add(spec);
                return;
            }
            if (spec == null) { inv.Warnings.Add($"{use.File}:{use.Line}: {apiName} call not understood (name isn't a string literal?)"); return; }
            if (spec.Type == null) { inv.Add(new DynamicDep(spec.Name ?? "?", apiName), use); return; }
            if (baseline.IsGame(spec.Type)) inv.Add(new ReflectionDep(spec), use);
        }

        // Reads the arguments back from the IL: [ldtoken T; call GetTypeFromHandle;] ldstr "name" [; new[] { typeof(A), ... }] call.
        private static LookupSpec? Parse(Instruction call, MethodReference mr, Api api, string apiName)
        {
            var spec = new LookupSpec { Kind = api.Kind, DeclaredOnly = api.Declared, ZeroParamFallback = api.ZeroFallback, Api = apiName };
            var argTypes = new List<TypeReference>();
            bool hasArgs = false;
            Instruction? ldstr = null;
            int steps = 0;
            for (var p = call.Previous; p != null && steps++ < 80; p = p.Previous)
            {
                if (p.OpCode == OpCodes.Ldstr) { ldstr = p; break; }
                if (p.OpCode == OpCodes.Ldtoken && p.Operand is TypeReference tr) argTypes.Add(tr);
                else if (p.OpCode == OpCodes.Newarr) hasArgs = true;
                else if (p.OpCode == OpCodes.Ldsfld && p.Operand is FieldReference f && f.Name == "EmptyTypes") hasArgs = true;
                else if (p.OpCode.FlowControl == FlowControl.Call && !IsGetTypeFromHandle(p)) return null;
            }
            if (ldstr == null) return null;
            spec.Name = (string)ldstr.Operand;
            if (hasArgs) { argTypes.Reverse(); spec.Args = argTypes; }
            if (api.Dynamic) return spec;
            var q = ldstr.Previous;
            if (q != null && IsGetTypeFromHandle(q) && q.Previous?.OpCode == OpCodes.Ldtoken && q.Previous.Operand is TypeReference owner)
                spec.Type = owner;
            else if (mr is GenericInstanceMethod gim && gim.GenericArguments.Count > 0 && mr.Name.Contains("FieldRefAccess"))
                spec.Type = gim.GenericArguments[0];
            return spec;
        }
    }

    // ---- enum members and numbers in our sources (they're compiled in as plain numbers) ----

    private static string StripComment(string line)
    {
        bool inStr = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
            else if (!inStr && c == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    private static List<TypeDefinition> PreferGame(IEnumerable<TypeDefinition> enums) =>
        enums.OrderBy(e => e.Module.Assembly.Name.Name == "Assembly-CSharp" ? 0 : 1).ToList();

    private static void ScanSources(Inventory inv, GameBuild baseline)
    {
        var ours = inv.Module.GetTypes().Select(t => t.Name).ToHashSet();
        var chain = new Regex(@"[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)+");
        var cast = new Regex(@"\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\(?\s*(-?\d+)\b");
        foreach (var file in Directory.EnumerateFiles(inv.SrcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(inv.SrcRoot, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = StripComment(lines[i]);
                foreach (Match m in chain.Matches(line))
                {
                    var parts = m.Value.Split('.').Select(x => x.Trim()).ToArray();
                    for (int j = 0; j + 1 < parts.Length; j++)
                    {
                        if (ours.Contains(parts[j])) continue;
                        var e = PreferGame(baseline.EnumsNamed(parts[j]))
                            .FirstOrDefault(x => x.Fields.Any(f => f.IsStatic && f.HasConstant && f.Name == parts[j + 1]));
                        if (e == null) continue;
                        var fld = e.Fields.First(f => f.IsStatic && f.Name == parts[j + 1]);
                        inv.Add(new EnumMemberDep(e, fld.Name, Convert.ToInt64(fld.Constant)), new Use(rel, i + 1, ""));
                    }
                }
                foreach (Match m in cast.Matches(line))
                {
                    if (ours.Contains(m.Groups[1].Value)) continue;
                    var e = PreferGame(baseline.EnumsNamed(m.Groups[1].Value)).FirstOrDefault();
                    if (e == null || !long.TryParse(m.Groups[2].Value, out long v)) continue;
                    inv.Add(new EnumLiteralDep(e, v), new Use(rel, i + 1, ""));
                }
            }
        }
    }

    // ---- ea-hints.txt: dependencies the IL can't show (dynamic lookups) ----
    // kind | Type.Full.Name | member | param types (comma-separated full names, empty: any) | our file[:line] | note

    private static void ReadHints(Inventory inv, string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var p = line.Split('|').Select(x => x.Trim()).ToArray();
            if (p.Length < 5) { inv.Warnings.Add("ea-hints.txt: bad line: " + raw); continue; }
            string[]? args = p[3].Length == 0 ? null : p[3] == "()" ? Array.Empty<string>() : p[3].Split(',').Select(x => x.Trim()).ToArray();
            var where = p[4].Split(':');
            int.TryParse(where.Length > 1 ? where[1] : "0", out int ln);
            inv.Add(new HintDep(p[0], p[1], p[2], args, p.Length > 5 ? p[5] : ""), new Use(where[0], ln, "(ea-hints.txt)"));
        }
    }
}

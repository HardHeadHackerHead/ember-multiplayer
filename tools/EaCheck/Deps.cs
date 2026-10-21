using Mono.Cecil;

namespace EaCheck;

enum Severity { Critical, High, Medium, Low, Info }

sealed record Use(string File, int Line, string Method)
{
    public bool TestOnly => Features.TestOnly(File);
    public string Feature => Features.Of(File);
}

sealed record Problem(Severity Severity, string Title, string Detail);

abstract class Dep
{
    public string Key = "";
    public readonly List<Use> Uses = new();
    private readonly HashSet<(string, int)> seen = new();

    public abstract string Category { get; }   // report column
    public abstract string Display { get; }
    public virtual string Assembly => "";
    public virtual string Extra => "";         // inventory detail (bound parameters, value...)
    public abstract IEnumerable<Problem> Check(GameBuild target, GameBuild baseline);

    public void AddUse(Use u)
    {
        if (seen.Add((u.File, u.Line))) Uses.Add(u);
    }

    public bool TestOnly => Uses.Count > 0 && Uses.All(u => u.TestOnly);
    public IEnumerable<string> FeatureNames => Uses.Select(u => u.Feature).Distinct();
}

// ---------------- plain metadata references in our IL ----------------

sealed class TypeDep : Dep
{
    public readonly TypeReference Type;
    public TypeDep(TypeReference t) { Type = t; Key = "T|" + Names.Scope(t) + "|" + t.FullName; }
    public override string Category => "Type";
    public override string Display => Names.Full(Type);
    public override string Assembly => Names.Scope(Type) ?? "";

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        var def = target.Resolve(Type);
        if (def == null)
        {
            var elsewhere = target.FindType(Type.FullName);
            if (elsewhere.Count > 0)
                yield return new(Severity.Critical, $"type {Display} moved to {elsewhere[0].Module.Assembly.Name.Name}.dll",
                    $"our IL looks for it in {Assembly}.dll: every class/method using it fails to load (a rebuild against the new build fixes it)");
            else
                yield return new(Severity.Critical, $"type {Display} is missing from {Assembly}.dll",
                    "every one of our classes/methods using it fails to load (TypeLoadException)");
            yield break;
        }
        if (ReferenceEquals(target, baseline)) yield break;
        var old = baseline.Resolve(Type);
        if (old == null) yield break;
        string Kind(TypeDefinition d) => d.IsEnum ? "enum" : d.IsInterface ? "interface" : d.IsValueType ? "struct" : "class";
        if (Kind(old) != Kind(def))
            yield return new(Severity.Critical, $"type {Display} changed from {Kind(old)} to {Kind(def)}", "IL using it is invalid (boxing, field layout, calls)");
        else if (old.IsEnum && old.Fields.First(f => f.Name == "value__").FieldType.FullName != def.Fields.First(f => f.Name == "value__").FieldType.FullName)
            yield return new(Severity.High, $"enum {Display} changed its underlying type", "");
    }
}

sealed class MethodDep : Dep
{
    public readonly MethodReference Method;
    public MethodDep(MethodReference m)
    {
        Method = m;
        Key = $"M|{Names.Scope(m.DeclaringType)}|{Names.Element(m.DeclaringType).FullName}::{m.Name}`{m.GenericParameters.Count}({Names.ParamSig(m)}){m.ReturnType.FullName}";
    }
    public override string Category => "Method";
    public override string Display => Names.Method(Method);
    public override string Assembly => Names.Scope(Method.DeclaringType) ?? "";

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        var type = target.Resolve(Method.DeclaringType);
        if (type == null) yield break; // reported by the type
        var def = target.Resolve(Method);
        if (def == null)
        {
            yield return new(Severity.High, $"method {Display} is missing or its signature changed",
                "methods of ours that call it throw MissingMethodException when first run" + Lookup.Candidates(target, type, Method.Name));
            yield break;
        }
        if (def.IsStatic == Method.HasThis)
            yield return new(Severity.High, $"method {Display} changed between static and instance", "calls to it fail");
        // Optional parameters' defaults are copied into our IL at compile time.
        var old = ReferenceEquals(target, baseline) ? null : baseline.Resolve(Method);
        if (old != null)
            foreach (var (o, n) in old.Parameters.Zip(def.Parameters))
                if (o.HasConstant && (!n.HasConstant || !Equals(o.Constant, n.Constant)))
                    yield return new(Severity.Medium, $"method {Display}: default of '{o.Name}' changed from {Show(o.Constant)} to {(n.HasConstant ? Show(n.Constant) : "none")}",
                        "the built mod still passes the old default where it leaves the argument out; rebuild");
    }

    private static string Show(object? v) => v is string s ? "\"" + s + "\"" : v?.ToString() ?? "null";

    public override string Extra
    {
        get
        {
            var d = Method.Resolve() is MethodDefinition m ? m.Parameters.Where(p => p.HasConstant).Select(p => $"{p.Name} = {Show(p.Constant)}").ToList() : new();
            return d.Count == 0 ? "" : "defaults compiled in: " + string.Join(", ", d);
        }
    }
}

sealed class FieldDep : Dep
{
    public readonly FieldReference Field;
    public readonly bool StaticUse;
    public FieldDep(FieldReference f, bool staticUse)
    {
        Field = f; StaticUse = staticUse;
        Key = $"F|{Names.Scope(f.DeclaringType)}|{Names.Element(f.DeclaringType).FullName}::{f.Name}:{f.FieldType.FullName}";
    }
    public override string Category => "Field";
    public override string Display => $"{Names.Full(Field.DeclaringType)}.{Field.Name} : {Names.Pretty(Field.FieldType)}";
    public override string Assembly => Names.Scope(Field.DeclaringType) ?? "";

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        var type = target.Resolve(Field.DeclaringType);
        if (type == null) yield break;
        var def = target.Resolve(Field);
        if (def == null)
        {
            FieldDefinition? byName = null;
            for (var cur = type; cur != null && byName == null; cur = target.Base(cur)) byName = cur.Fields.FirstOrDefault(f => f.Name == Field.Name);
            if (byName != null)
                yield return new(Severity.High, $"field {Display}: type changed to {Names.Pretty(byName.FieldType)}",
                    "methods of ours that use it throw MissingFieldException when first run");
            else
                yield return new(Severity.High, $"field {Display} is missing",
                    "methods of ours that use it throw MissingFieldException when first run" + Lookup.Candidates(target, type, Field.Name));
            yield break;
        }
        if (def.IsStatic != StaticUse)
            yield return new(Severity.High, $"field {Display} changed between static and instance", "");
    }
}

// Our class implements a game interface or derives from an abstract game class: new abstract members stop it loading.
sealed class ImplementsDep : Dep
{
    public readonly TypeDefinition Ours;
    public readonly TypeReference Game;
    public readonly bool IsInterface;
    public ImplementsDep(TypeDefinition ours, TypeReference game, bool isInterface)
    {
        Ours = ours; Game = game; IsInterface = isInterface;
        Key = $"I|{ours.FullName}|{game.FullName}";
    }
    public override string Category => "Implements";
    public override string Display => $"{Names.Full(Ours)} {(IsInterface ? "implements" : "derives from")} {Names.Full(Game)}";
    public override string Assembly => Names.Scope(Game) ?? "";

    private List<MethodDefinition> OurMethods()
    {
        var list = new List<MethodDefinition>();
        for (TypeDefinition? t = Ours; t != null; t = t.BaseType is TypeDefinition bt ? bt : null) list.AddRange(t.Methods);
        return list;
    }

    private static bool SameParams(MethodDefinition a, MethodDefinition b) =>
        a.Parameters.Count == b.Parameters.Count &&
        a.Parameters.Zip(b.Parameters).All(p => p.Second.ParameterType.ContainsGenericParameter || p.First.ParameterType.ContainsGenericParameter ||
                                                p.First.ParameterType.FullName == p.Second.ParameterType.FullName);

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        if (Ours.IsAbstract || Ours.IsInterface) yield break;
        var def = target.Resolve(Game);
        if (def == null) yield break;
        var ours = OurMethods();
        var required = new List<MethodDefinition>();
        if (IsInterface) required.AddRange(def.Methods.Where(m => m.IsAbstract && !m.IsStatic));
        else
        {
            // abstract methods of the game chain not implemented further down that chain
            var chain = new List<TypeDefinition>();
            for (var cur = def; cur != null && target.GameAssemblies.Contains(cur.Module.Assembly.Name.Name); cur = target.Base(cur)) chain.Add(cur);
            for (int i = 0; i < chain.Count; i++)
                foreach (var m in chain[i].Methods.Where(m => m.IsAbstract))
                    if (!chain.Take(i).Any(c => c.Methods.Any(o => !o.IsAbstract && o.Name == m.Name && SameParams(o, m)))) required.Add(m);
        }
        foreach (var m in required)
            if (!ours.Any(o => (o.Name == m.Name || o.Name.EndsWith("." + m.Name)) && SameParams(o, m)))
                yield return new(Severity.Critical, $"{Names.Full(Ours)} doesn't implement {Names.Method(m)}",
                    $"new abstract member in {Names.Full(Game)}: our class fails to load (TypeLoadException)");
    }
}

// ---------------- hooks and lookups by name ----------------

sealed class HarmonyPatchDep : Dep
{
    public readonly TypeDefinition PatchClass;
    public readonly List<(LookupSpec Spec, string Origin)> Targets = new();
    public readonly List<MethodDefinition> PatchMethods = new();
    public readonly List<LookupSpec> Anchors = new();   // members the transpiler looks for in the target's body
    public readonly List<string> Notes = new();
    public HarmonyPatchDep(TypeDefinition patchClass) { PatchClass = patchClass; Key = "H|" + patchClass.FullName; }
    public override string Category => "Harmony";
    public override string Display => $"{PatchClass.Name} → {string.Join("; ", Targets.Select(t => t.Spec.Describe()))}";
    public override string Assembly => Targets.Count > 0 && Targets[0].Spec.Type != null ? Names.Scope(Targets[0].Spec.Type!) ?? "" : "";

    public override string Extra
    {
        get
        {
            var parts = PatchMethods.Select(m => m.Name + (m.Name == "Transpiler" || m.Parameters.Count == 0 ? "" : "(" + string.Join(", ", m.Parameters.Select(p => p.Name)) + ")"));
            var s = string.Join(", ", parts);
            if (Anchors.Count > 0) s += "; transpiler looks for " + string.Join(", ", Anchors.Select(a => a.Describe()));
            if (Notes.Count > 0) s += "; " + string.Join("; ", Notes);
            return s;
        }
    }

    private static readonly HashSet<string> Special = new() { "__state", "__args", "__originalMethod", "__runOriginal", "__exception" };

    private static TypeReference Unref(TypeReference t) => t is ByReferenceType b ? b.ElementType : t;

    private IEnumerable<Problem> CheckParams(MethodDefinition patch, MethodDefinition target, GameBuild build)
    {
        string where = $"{PatchClass.Name}.{patch.Name}";
        foreach (var p in patch.Parameters)
        {
            string n = p.Name;
            if (Special.Contains(n)) continue;
            if (n == "__instance")
            {
                if (target.IsStatic) yield return new(Severity.Medium, $"{where}: __instance, but {Names.Method(target)} is now static", "gets null");
                continue;
            }
            if (n == "__result")
            {
                if (target.ReturnType.FullName == "System.Void" && !target.IsConstructor)
                    yield return new(Severity.High, $"{where}: __result, but {Names.Method(target)} now returns void", "Harmony refuses the patch class");
                else if (TypeDiffers(Unref(p.ParameterType), target.ReturnType))
                    yield return new(Severity.Medium, $"{where}: __result is {Names.Pretty(Unref(p.ParameterType))}, {Names.Method(target)} now returns {Names.Pretty(target.ReturnType)}", "");
                continue;
            }
            if (n.StartsWith("___"))
            {
                var (f, err) = Lookup.Resolve(build, new LookupSpec { Kind = MemberKind.Field, Type = target.DeclaringType, Name = n[3..] });
                if (f == null) yield return new(Severity.High, $"{where}: injected field {n} not found on {Names.Full(target.DeclaringType)}", err + "; Harmony refuses the patch class");
                continue;
            }
            if (n.Length > 2 && n.StartsWith("__") && int.TryParse(n[2..], out int idx))
            {
                if (idx >= target.Parameters.Count) yield return new(Severity.High, $"{where}: {n}, but {Names.Method(target)} has {target.Parameters.Count} parameters", "");
                continue;
            }
            var tp = target.Parameters.FirstOrDefault(x => x.Name == n);
            if (tp == null)
                yield return new(Severity.High, $"{where}: parameter '{n}' no longer exists in {Names.Method(target)}",
                    $"its parameters are now ({Names.Params(target)}); Harmony refuses the patch class");
            else if (TypeDiffers(Unref(p.ParameterType), Unref(tp.ParameterType)))
                yield return new(Severity.Medium, $"{where}: parameter '{n}' is {Names.Pretty(Unref(p.ParameterType))} in our patch, {Names.Pretty(Unref(tp.ParameterType))} in the game", "");
        }
    }

    private static bool TypeDiffers(TypeReference ours, TypeReference theirs) =>
        ours.FullName != "System.Object" && !theirs.ContainsGenericParameter && !ours.ContainsGenericParameter && ours.FullName != theirs.FullName;

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        if (Targets.Count == 0)
        {
            yield return new(Severity.Info, $"{PatchClass.Name}: target not statically known", "check this hook by hand");
            yield break;
        }
        foreach (var (spec, origin) in Targets)
        {
            var (found, err) = Lookup.Resolve(target, spec);
            if (found == null)
            {
                yield return new(Severity.High, $"hook {PatchClass.Name} can't find {spec.Describe()}",
                    $"{err} ({origin}); the whole patch class is skipped: `[patch]` error in the log, every self-test fails its hooks check");
                continue;
            }
            if (found is not MethodDefinition m) continue;
            if (!ReferenceEquals(target, baseline) && Lookup.Resolve(baseline, spec).found is MethodDefinition old && Names.ParamSig(old) != Names.ParamSig(m))
                yield return new(Severity.Medium, $"hook {PatchClass.Name} now takes a different overload: {Names.Method(m)}", $"was {Names.Method(old)}");
            foreach (var pm in PatchMethods.Where(x => x.Name != "Transpiler" && x.Name != "ILManipulator"))
                foreach (var p in CheckParams(pm, m, target)) yield return p;
            foreach (var a in Anchors)
            {
                var (am, aerr) = Lookup.Resolve(target, a);
                if (am == null) yield return new(Severity.High, $"{PatchClass.Name}.Transpiler: {a.Describe()} not found", aerr ?? "");
                else if (!Lookup.BodyUses(m, am))
                    yield return new(Severity.Medium, $"{PatchClass.Name}.Transpiler: {Names.Method(m)} no longer uses {a.Describe()}",
                        "the transpiler finds nothing to replace and silently changes nothing");
            }
        }
    }
}

sealed class ReflectionDep : Dep
{
    public readonly LookupSpec Spec;
    public ReflectionDep(LookupSpec s) { Spec = s; Key = "R|" + s.Key; }
    public override string Category => "Reflection";
    public override string Display => Spec.Describe();
    public override string Extra => Spec.Api;
    public override string Assembly => Spec.Type == null ? "" : Names.Scope(Spec.Type) ?? "";

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        if (target.Resolve(Spec.Type!) == null) yield break; // reported by the type
        var (found, err) = Lookup.Resolve(target, Spec);
        if (found == null)
            yield return new(Severity.High, $"lookup by name fails: {Spec.Describe()}",
                $"{err}; {Spec.Api} returns null or throws (in a static initializer: the whole class fails)");
    }
}

// A name looked up on an object whose type isn't known statically (Traverse.Create(x).Field("..."), obj.GetType().GetMethod("...")).
sealed class DynamicDep : Dep
{
    public readonly string Name, Api;
    public bool Covered;
    public DynamicDep(string name, string api) { Name = name; Api = api; Key = $"D|{api}|{name}"; }
    public override string Category => "Reflection";
    public override string Display => $"<dynamic>.{Name}";
    public override string Extra => Api + (Covered ? " (checked through ea-hints.txt)" : " (NOT checked: add it to tools/EaCheck/ea-hints.txt)");

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        if (!Covered) yield return new(Severity.Info, $"unchecked dynamic lookup \"{Name}\" ({Api})", "add the member it means to tools/EaCheck/ea-hints.txt");
    }
}

// A dependency listed by hand in ea-hints.txt (for dynamic lookups the IL can't tell).
sealed class HintDep : Dep
{
    public readonly string Kind, TypeName, Member, Note;
    public readonly string[]? Params;
    public HintDep(string kind, string type, string member, string[]? args, string note)
    {
        Kind = kind; TypeName = type; Member = member; Params = args; Note = note;
        Key = $"X|{kind}|{type}|{member}|{(args == null ? "" : string.Join(",", args))}";
    }
    public override string Category => "Reflection";
    public override string Display => $"{TypeName}.{Member}{(Params == null ? "" : "(" + string.Join(", ", Params) + ")")} ({Kind}, from ea-hints.txt)";
    public override string Extra => Note;

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        var types = target.FindType(TypeName);
        if (types.Count == 0) { yield return new(Severity.High, $"type {TypeName} is missing (ea-hints.txt)", Note); yield break; }
        var t = types[0];
        bool ok = false;
        for (var cur = t; cur != null && !ok; cur = target.Base(cur))
        {
            ok = Kind switch
            {
                "field" => cur.Fields.Any(f => f.Name == Member),
                "property" => cur.Properties.Any(p => p.Name == Member),
                "method" => cur.Methods.Any(m => m.Name == Member && (Params == null || m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(Params))),
                "type" => true,
                _ => false,
            };
        }
        if (!ok) yield return new(Severity.High, $"{Display} is missing", Note + Lookup.Candidates(target, t, Member));
    }
}

// ---------------- values compiled into our code ----------------

sealed class EnumMemberDep : Dep
{
    public readonly string EnumName, Member, AssemblyName;
    public readonly long Value;
    public EnumMemberDep(TypeDefinition e, string member, long value)
    {
        EnumName = e.FullName; Member = member; Value = value; AssemblyName = e.Module.Assembly.Name.Name;
        Key = $"E|{AssemblyName}|{EnumName}.{member}";
    }
    public override string Category => "Enum value";
    public override string Display => $"{EnumName.Replace('/', '.')}.{Member}";
    public override string Extra => "= " + Value;
    public override string Assembly => AssemblyName;

    public static string NamesOf(TypeDefinition e, long v)
    {
        var n = e.Fields.Where(f => f.IsStatic && f.HasConstant && Convert.ToInt64(f.Constant) == v).Select(f => f.Name).ToList();
        return n.Count == 0 ? "no member" : string.Join("/", n);
    }

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        var e = target.FindEnum(EnumName);
        if (e == null) { yield return new(Severity.High, $"enum {EnumName} is missing", $"the built mod still uses {Member} = {Value}"); yield break; }
        var f = e.Fields.FirstOrDefault(x => x.IsStatic && x.Name == Member);
        if (f == null)
            yield return new(Severity.High, $"{Display} is gone",
                $"the built mod still uses the number {Value}, which in this build means: {NamesOf(e, Value)} (rebuilding fails until the code changes)");
        else if (Convert.ToInt64(f.Constant) != Value)
            yield return new(Severity.High, $"{Display} changed value {Value} → {Convert.ToInt64(f.Constant)}",
                $"the built mod still uses {Value}, which in this build means: {NamesOf(e, Value)}; rebuild to pick up the new value (saves/flags written with the old number mean something else)");
    }
}

// (SomeEnum)123 in our source.
sealed class EnumLiteralDep : Dep
{
    public readonly string EnumName, BaselineNames;
    public readonly long Value;
    public EnumLiteralDep(TypeDefinition e, long value)
    {
        EnumName = e.FullName; Value = value; BaselineNames = EnumMemberDep.NamesOf(e, value);
        Key = $"L|{EnumName}|{value}";
    }
    public override string Category => "Enum value";
    public override string Display => $"({EnumName.Replace('/', '.')}){Value}";
    public override string Extra => "number written in our code; in the demo it means: " + BaselineNames;

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        var e = target.FindEnum(EnumName);
        if (e == null) yield break;
        var now = EnumMemberDep.NamesOf(e, Value);
        if (now != BaselineNames)
            yield return new(Severity.Medium, $"{Display} now means {now} (was {BaselineNames})", "a number written in our code");
    }
}

sealed class SceneDep : Dep
{
    public readonly string Scene;
    public SceneDep(string scene) { Scene = scene; Key = "S|" + scene; }
    public override string Category => "Scene";
    public override string Display => Scene;

    public override IEnumerable<Problem> Check(GameBuild target, GameBuild baseline)
    {
        if (target.Scenes == null) { yield return new(Severity.Info, $"scene \"{Scene}\" not checked", "no globalgamemanagers next to Managed"); yield break; }
        if (!target.Scenes.Contains(Scene))
            yield return new(Severity.Medium, $"scene \"{Scene}\" is not in this build",
                "code comparing scene names or loading it never matches/fails; build scenes: " + string.Join(", ", target.Scenes));
    }
}

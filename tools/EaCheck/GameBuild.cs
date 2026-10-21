using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace EaCheck;

// One game build's managed assemblies (read-only, loaded in memory: the game files are never locked or written)
// plus the scene list from globalgamemanagers.
sealed class GameBuild : IAssemblyResolver
{
    public readonly string Root;
    public readonly string ManagedDir;
    public readonly string? DataDir;
    public readonly HashSet<string> GameAssemblies = new(StringComparer.OrdinalIgnoreCase);
    public readonly MetadataResolver Meta;
    public readonly List<string>? Scenes;
    private readonly Dictionary<string, AssemblyDefinition?> cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<TypeDefinition>>? byFullName;
    private Dictionary<string, List<TypeDefinition>>? enumsBySimpleName;

    // A game folder (with <Game>_Data\Managed) or a Managed folder.
    public static GameBuild Open(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(Path.Combine(path, "Assembly-CSharp.dll"))) return new GameBuild(path, path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var data = Directory.GetDirectories(path, "*_Data").FirstOrDefault(d => File.Exists(Path.Combine(d, "Managed", "Assembly-CSharp.dll")));
        if (data == null) throw new FileNotFoundException($"no <Game>_Data\\Managed\\Assembly-CSharp.dll under {path} (an IL2CPP build has none: BepInEx 5 can't mod it either)");
        return new GameBuild(path, Path.Combine(data, "Managed"));
    }

    private GameBuild(string root, string managed)
    {
        Root = root;
        ManagedDir = managed;
        var data = Directory.GetParent(managed)!.FullName;
        DataDir = File.Exists(Path.Combine(data, "globalgamemanagers")) ? data : null;
        foreach (var f in Directory.GetFiles(managed, "*.dll"))
        {
            var n = Path.GetFileNameWithoutExtension(f);
            if (!IsSystem(n)) GameAssemblies.Add(n);
        }
        Meta = new MetadataResolver(this);
        if (DataDir != null) Scenes = ReadScenes(Path.Combine(DataDir, "globalgamemanagers"));
    }

    public static bool IsSystem(string n) =>
        n is "mscorlib" or "netstandard" || n.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);

    public bool IsGame(TypeReference t)
    {
        var s = Names.Scope(t);
        return s != null && GameAssemblies.Contains(s);
    }

    // The build's scene list (Build Settings), from the scene paths stored in globalgamemanagers.
    public static List<string> ReadScenes(string file)
    {
        var text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
        return Regex.Matches(text, @"(?:Assets|Packages)/[\x20-\x7e]{1,240}?\.unity")
            .Select(m => Path.GetFileNameWithoutExtension(m.Value)).Distinct().ToList();
    }

    public string Describe()
    {
        var asm = Path.Combine(ManagedDir, "Assembly-CSharp.dll");
        var fi = new FileInfo(asm);
        string mvid = "";
        try { mvid = Load("Assembly-CSharp")?.MainModule.Mvid.ToString() ?? ""; } catch { }
        string version = "";
        if (DataDir != null)
        {
            var appInfo = Path.Combine(DataDir, "app.info");
            if (File.Exists(appInfo)) version = string.Join(" / ", File.ReadAllLines(appInfo).Where(l => l.Length > 0));
        }
        return $"`{Root}` (Assembly-CSharp.dll {fi.Length:N0} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm}, MVID {mvid}{(version == "" ? "" : ", app.info: " + version)})";
    }

    public AssemblyDefinition? Load(string name)
    {
        if (cache.TryGetValue(name, out var a)) return a;
        var file = Path.Combine(ManagedDir, name + ".dll");
        a = null;
        if (File.Exists(file))
        {
            try { a = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = this, InMemory = true, ReadingMode = ReadingMode.Deferred }); }
            catch (BadImageFormatException) { a = null; }
        }
        cache[name] = a;
        return a;
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name) => Load(name.Name)!;
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => Load(name.Name)!;
    public void Dispose() { }

    public TypeDefinition? Resolve(TypeReference t)
    {
        try { return Meta.Resolve(t); } catch { return null; }
    }

    public MethodDefinition? Resolve(MethodReference m)
    {
        try { return Meta.Resolve(m); } catch { return null; }
    }

    public FieldDefinition? Resolve(FieldReference f)
    {
        try { return Meta.Resolve(f); } catch { return null; }
    }

    public TypeDefinition? Base(TypeDefinition t) => t.BaseType == null ? null : Resolve(t.BaseType);

    private void Index()
    {
        if (byFullName != null) return;
        byFullName = new();
        enumsBySimpleName = new();
        foreach (var n in GameAssemblies.OrderBy(x => x, StringComparer.Ordinal))
        {
            AssemblyDefinition? a;
            try { a = Load(n); } catch { continue; }
            if (a == null) continue;
            foreach (var t in a.MainModule.GetTypes())
            {
                if (!byFullName.TryGetValue(t.FullName, out var l)) byFullName[t.FullName] = l = new();
                l.Add(t);
                if (t.IsEnum)
                {
                    if (!enumsBySimpleName.TryGetValue(t.Name, out var e)) enumsBySimpleName[t.Name] = e = new();
                    e.Add(t);
                }
            }
        }
    }

    // Cecil full name (nested types as Outer/Inner), in any game assembly.
    public IReadOnlyList<TypeDefinition> FindType(string fullName)
    {
        Index();
        return byFullName!.TryGetValue(fullName, out var l) ? l : Array.Empty<TypeDefinition>();
    }

    public IReadOnlyList<TypeDefinition> EnumsNamed(string simpleName)
    {
        Index();
        return enumsBySimpleName!.TryGetValue(simpleName, out var l) ? l : Array.Empty<TypeDefinition>();
    }

    public TypeDefinition? FindEnum(string fullName) => FindType(fullName).FirstOrDefault(t => t.IsEnum);
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory.Engine.Workspace;

/// <summary>
/// The public signatures of types a piece of text names that come from the repository's NuGet packages rather than its
/// own source, such as a UI library's components. Without them an agent guesses at a package's namespaces, members and
/// return types from memory. Read from the compile assemblies that a restore lists in each project's
/// obj/project.assets.json, so it only knows packages once the repository has been restored or built.
/// </summary>
public static partial class PackageDefinitions
{
    private const int MaxAssetsFiles = 200;
    private const int MaxTypesPerName = 2;

    /// <summary>One public type in a package's compile assembly.</summary>
    private sealed record PackageType(string Package, string Version, string AssemblyPath, string FullName);

    private sealed record Index(string Stamp, IReadOnlyList<string> AssemblyPaths, Dictionary<string, List<PackageType>> Types);

    // By repository, rebuilt only when an assets file changes, which is when a restore changed the packages.
    private static readonly ConcurrentDictionary<string, Index> Indexes = new(StringComparer.OrdinalIgnoreCase);

    // A name that reads as code rather than a capitalised word: two or more capitals, so FluentSelect and IToastService
    // but not Scan, Cancel or Color.
    [GeneratedRegex(@"\b[A-Z][a-z0-9_]*[A-Z][A-Za-z0-9_]*\b")]
    private static partial Regex CodeLikeName();

    /// <summary>
    /// The signatures of the package types the text names, in the order it first names them, without the names in
    /// <paramref name="skip"/> (the repository's own types). Each comes back as a pseudo-file whose path names the
    /// package and type. Empty when the repository has no restored .NET projects.
    /// </summary>
    public static IReadOnlyList<ContextFile> Find(string repositoryPath, string text, IReadOnlySet<string> skip, int maxTypes, int maxCharsPerType)
    {
        if (maxTypes <= 0 || string.IsNullOrWhiteSpace(text)) return [];
        var index = GetIndex(repositoryPath);
        if (index is null || index.Types.Count == 0) return [];

        var wanted = new List<PackageType>();
        foreach (var name in CodeLikeName().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal))
        {
            if (skip.Contains(name) || !index.Types.TryGetValue(name, out var types)) continue;
            wanted.AddRange(types.Take(MaxTypesPerName));
            if (wanted.Count >= maxTypes) break;
        }
        if (wanted.Count == 0) return [];

        var found = new List<ContextFile>();
        using var context = new MetadataLoadContext(new PathAssemblyResolver(ResolverPaths(index.AssemblyPaths)));
        var packagePaths = index.AssemblyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var type in wanted.Take(maxTypes))
        {
            string? rendered;
            try
            {
                var loaded = context.LoadFromAssemblyPath(type.AssemblyPath).GetType(type.FullName);
                rendered = loaded is null ? null : Render(loaded, type, packagePaths, maxCharsPerType);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or TypeLoadException or NotSupportedException or InvalidOperationException)
            {
                rendered = null;
            }
            if (rendered is not null)
                found.Add(new ContextFile($"package {type.Package} {type.Version}: {DisplayName(type.FullName)}", rendered, false));
        }
        return found;
    }

    /// <summary>The names of every public type in the repository's packages, such as FluentSelect.</summary>
    public static IReadOnlySet<string> KnownNames(string repositoryPath) =>
        GetIndex(repositoryPath)?.Types.Keys.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();

    private static Index? GetIndex(string repositoryPath)
    {
        if (!Directory.Exists(repositoryPath)) return null;
        var root = new DirectoryInfo(repositoryPath);
        var key = Path.TrimEndingDirectorySeparator(root.FullName);
        var assetsFiles = AssetsFiles(root).Take(MaxAssetsFiles).ToList();
        if (assetsFiles.Count == 0) return null;

        var stamp = string.Join('|', assetsFiles.Select(f => $"{f.FullName}:{f.LastWriteTimeUtc.Ticks}:{f.Length}"));
        if (Indexes.TryGetValue(key, out var cached) && cached.Stamp == stamp) return cached;

        var assemblies = new Dictionary<string, (string Package, string Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in assetsFiles)
        {
            foreach (var (package, version, path) in CompileAssemblies(file.FullName))
                assemblies.TryAdd(path, (package, version));
        }

        var types = new Dictionary<string, List<PackageType>>(StringComparer.Ordinal);
        foreach (var (path, (package, version)) in assemblies)
        {
            foreach (var fullName in PublicTypeNames(path))
            {
                var name = SimpleName(fullName);
                if (!types.TryGetValue(name, out var list)) types[name] = list = [];
                list.Add(new PackageType(package, version, path, fullName));
            }
        }

        var index = new Index(stamp, assemblies.Keys.ToList(), types);
        Indexes[key] = index;
        return index;
    }

    /// <summary>Each project's obj/project.assets.json, skipping the folders the repository scan skips.</summary>
    private static IEnumerable<FileInfo> AssetsFiles(DirectoryInfo directory)
    {
        var assets = new FileInfo(Path.Combine(directory.FullName, "obj", "project.assets.json"));
        if (assets.Exists) yield return assets;

        IEnumerable<DirectoryInfo> children;
        try { children = directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
        {
            if (RepositoryScanner.IsIgnoredDirectory(child)) continue;
            foreach (var file in AssetsFiles(child)) yield return file;
        }
    }

    /// <summary>The compile assemblies of every package a restore resolved, for every target framework.</summary>
    internal static IEnumerable<(string Package, string Version, string Path)> CompileAssemblies(string assetsPath)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(assetsPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { yield break; }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("targets", out var targets) || !root.TryGetProperty("libraries", out var libraries)) yield break;
            var folders = root.TryGetProperty("packageFolders", out var packageFolders)
                ? packageFolders.EnumerateObject().Select(f => f.Name).ToList()
                : [];

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                    if (!library.Value.TryGetProperty("compile", out var compile)) continue;
                    if (!libraries.TryGetProperty(library.Name, out var info) || !info.TryGetProperty("path", out var relative)) continue;

                    var slash = library.Name.IndexOf('/');
                    var (package, version) = slash > 0 ? (library.Name[..slash], library.Name[(slash + 1)..]) : (library.Name, "");
                    foreach (var asset in compile.EnumerateObject())
                    {
                        if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        var path = folders
                            .Select(folder => Path.GetFullPath(Path.Combine(folder, relative.GetString() ?? "", asset.Name)))
                            .FirstOrDefault(File.Exists);
                        if (path is not null) yield return (package, version, path);
                    }
                }
            }
        }
    }

    /// <summary>The full names of an assembly's public top-level types, read straight from its metadata.</summary>
    private static IEnumerable<string> PublicTypeNames(string path)
    {
        var names = new List<string>();
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return names;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                if ((definition.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) continue;
                var name = reader.GetString(definition.Name);
                if (name.Contains('<')) continue;
                var ns = reader.GetString(definition.Namespace);
                names.Add(ns.Length == 0 ? name : $"{ns}.{name}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        return names;
    }

    /// <summary>
    /// One file per assembly name: the packages' own first, as the project compiles against them, then the shared
    /// frameworks for the rest. Two files of one name make the load context refuse the second.
    /// </summary>
    private static IEnumerable<string> ResolverPaths(IEnumerable<string> packageAssemblies)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in packageAssemblies.Concat(FrameworkAssemblies()))
        {
            if (names.Add(Path.GetFileNameWithoutExtension(path))) yield return path;
        }
    }

    /// <summary>
    /// The newest installed shared frameworks, all of one major version, so the base types and attributes package types
    /// use, such as ComponentBase, resolve. Mixing majors would leave a newer framework's references to a newer
    /// System.Runtime unresolved.
    /// </summary>
    private static IEnumerable<string> FrameworkAssemblies()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(runtime)));
        if (shared is null || !Directory.Exists(Path.Combine(shared, "Microsoft.NETCore.App")))
            return Directory.EnumerateFiles(runtime, "*.dll");

        var core = Versions(Path.Combine(shared, "Microsoft.NETCore.App")).FirstOrDefault();
        if (core.Path is null) return Directory.EnumerateFiles(runtime, "*.dll");
        var directories = new List<string> { core.Path };
        foreach (var framework in new[] { "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App" })
        {
            var match = Versions(Path.Combine(shared, framework)).FirstOrDefault(v => v.Version.Major <= core.Version.Major);
            if (match.Path is not null) directories.Add(match.Path);
        }
        return directories.SelectMany(d => Directory.EnumerateFiles(d, "*.dll"));
    }

    /// <summary>A shared framework's installed versions, newest first.</summary>
    private static IEnumerable<(string Path, Version Version)> Versions(string folder) =>
        !Directory.Exists(folder)
            ? []
            : Directory.GetDirectories(folder)
                .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                .Where(d => d.Version is not null)
                .Select(d => (d.Path, d.Version!))
                .OrderByDescending(d => d.Item2);

    private static string SimpleName(string fullName)
    {
        var name = fullName[(fullName.LastIndexOf('.') + 1)..];
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    private static string DisplayName(string fullName)
    {
        var tick = fullName.IndexOf('`');
        return tick < 0 ? fullName : fullName[..tick];
    }

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static string Render(Type type, PackageType source, IReadOnlySet<string> packagePaths, int maxChars)
    {
        var builder = new StringBuilder();
        builder.Append("// From the ").Append(source.Package).Append(' ').Append(source.Version)
            .Append(" package: its public members, without bodies.\n");
        if (type.Namespace is { Length: > 0 } ns) builder.Append("namespace ").Append(ns).Append(";\n\n");

        if (IsDelegate(type))
        {
            var invoke = type.GetMethod("Invoke");
            builder.Append("public delegate ").Append(invoke is null ? "void" : Format(invoke.ReturnType)).Append(' ')
                .Append(Format(type)).Append('(').Append(invoke is null ? "" : Parameters(invoke)).Append(");\n");
            return builder.ToString();
        }

        builder.Append(Header(type)).Append("\n{\n");
        var members = new List<string>();
        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                members.Add(Try(() => $"    {field.Name} = {field.GetRawConstantValue()},"));
        }
        else
        {
            members.AddRange(Members(type));
            // Members inherited from the package's own base types, such as a component's parameters, are part of
            // what code can use. Framework base types, such as ComponentBase, aren't repeated.
            for (var current = SafeBase(type); current is not null && packagePaths.Contains(current.Assembly.Location); current = SafeBase(current))
            {
                // Constructors aren't inherited, and an override says nothing new.
                var inherited = Members(current, constructors: false).Where(m => m.Length > 0 && !members.Contains(m)).ToList();
                if (inherited.Count == 0) continue;
                members.Add($"    // Inherited from {Format(current)}");
                members.AddRange(inherited);
            }
        }

        members.RemoveAll(m => m.Length == 0);
        var shown = 0;
        foreach (var member in members)
        {
            if (builder.Length + member.Length > maxChars)
            {
                builder.Append($"    // … {members.Count - shown} more members not shown\n");
                break;
            }
            // Interface members are public anyway, and C# writes them without the modifier.
            builder.Append(type.IsInterface ? member.Replace("    public ", "    ") : member).Append('\n');
            shown++;
        }
        return builder.Append("}\n").ToString();
    }

    private static IEnumerable<string> Members(Type type, bool constructors = true)
    {
        foreach (var constructor in constructors ? type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) : [])
            yield return Try(() => $"    public {SimpleName(type.Name)}({Parameters(constructor)});");

        foreach (var property in type.GetProperties(Declared))
        {
            yield return Try(() =>
            {
                var get = property.GetMethod is { IsPublic: true } getter ? getter : null;
                var set = property.SetMethod is { IsPublic: true } ? "set; " : "";
                if (get is null && set.Length == 0) return "";
                var attribute = HasAttribute(property, "ParameterAttribute") ? "[Parameter] "
                    : HasAttribute(property, "CascadingParameterAttribute") ? "[CascadingParameter] " : "";
                var isStatic = (get ?? property.SetMethod)!.IsStatic ? "static " : "";
                var indexer = property.GetIndexParameters();
                var name = indexer.Length > 0 ? $"this[{string.Join(", ", indexer.Select(Parameter))}]" : property.Name;
                return $"    {attribute}public {isStatic}{Format(property.PropertyType)} {name} {{ {(get is null ? "" : "get; ")}{set}}}";
            });
        }

        foreach (var field in type.GetFields(Declared))
        {
            yield return Try(() => field.IsLiteral
                ? $"    public const {Format(field.FieldType)} {field.Name} = {Literal(field.GetRawConstantValue())};"
                : $"    public {(field.IsStatic ? "static " : "")}{(field.IsInitOnly ? "readonly " : "")}{Format(field.FieldType)} {field.Name};");
        }

        foreach (var @event in type.GetEvents(Declared))
            yield return Try(() => $"    public event {Format(@event.EventHandlerType!)} {@event.Name};");

        foreach (var method in type.GetMethods(Declared))
        {
            if (method.IsSpecialName || method.Name.Contains('<') || method.Name is "Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "<Clone>$") continue;
            yield return Try(() =>
            {
                var generic = method.IsGenericMethodDefinition ? $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>" : "";
                var modifier = method.IsStatic ? "static " : type.IsInterface || !method.IsAbstract ? "" : "abstract ";
                return $"    public {modifier}{Format(method.ReturnType)} {method.Name}{generic}({Parameters(method)});";
            });
        }
    }

    private static string Header(Type type)
    {
        var kind = type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : type is { IsAbstract: true, IsSealed: true } ? "static class"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";
        var bases = new List<string>();
        if (!type.IsInterface && !type.IsValueType && SafeBase(type) is { } baseType && baseType.FullName != "System.Object")
            bases.Add(Format(baseType));
        try
        {
            var inherited = SafeBase(type)?.GetInterfaces() ?? [];
            bases.AddRange(type.GetInterfaces()
                .Where(i => !inherited.Contains(i))
                .Select(Format));
        }
        catch (Exception ex) when (ex is IOException or TypeLoadException) { }
        return $"public {kind} {Format(type)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}";
    }

    private static string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(Parameter));

    private static string Parameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var prefix = "";
        if (type.IsByRef)
        {
            prefix = parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ";
            type = type.GetElementType()!;
        }
        else if (HasAttribute(parameter, "ParamArrayAttribute"))
        {
            prefix = "params ";
        }
        if (parameter.Member is MethodInfo { IsStatic: true } && parameter.Position == 0 && HasAttribute(parameter.Member, "ExtensionAttribute"))
            prefix = "this " + prefix;

        var text = $"{prefix}{Format(type)} {parameter.Name}";
        if (parameter.IsOptional)
        {
            object? value;
            try { value = parameter.RawDefaultValue; }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException) { value = DBNull.Value; }
            text += value is DBNull or Missing ? " = default" : $" = {Literal(value)}";
        }
        return text;
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => JsonSerializer.Serialize(s),
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "default"
    };

    private static readonly Dictionary<string, string> Keywords = new()
    {
        ["System.Void"] = "void", ["System.Object"] = "object", ["System.String"] = "string", ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte", ["System.SByte"] = "sbyte", ["System.Char"] = "char", ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort", ["System.Int32"] = "int", ["System.UInt32"] = "uint", ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong", ["System.Single"] = "float", ["System.Double"] = "double", ["System.Decimal"] = "decimal"
    };

    internal static string Format(Type type)
    {
        if (type.IsByRef) return Format(type.GetElementType()!);
        if (type.IsArray) return $"{Format(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        if (type.IsPointer) return $"{Format(type.GetElementType()!)}*";
        if (type.IsGenericParameter) return type.Name;
        if (type.FullName is { } full && Keywords.TryGetValue(full, out var keyword)) return keyword;

        var prefix = type.IsNested && type.DeclaringType is { } outer ? Format(outer) + "." : "";
        if (!type.IsGenericType) return prefix + type.Name;

        var definition = type.IsGenericTypeDefinition ? type : type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if (definition.FullName == "System.Nullable`1") return Format(arguments[0]) + "?";
        // A nested type's arguments include its outer type's, which the outer name already shows.
        var own = type.IsNested && type.DeclaringType is { IsGenericType: true } declaring
            ? arguments.Skip(declaring.GetGenericArguments().Length).ToArray()
            : arguments;
        var name = SimpleName(type.Name);
        return own.Length == 0 ? prefix + name : $"{prefix}{name}<{string.Join(", ", own.Select(Format))}>";
    }

    private static bool IsDelegate(Type type)
    {
        try { return SafeBase(type)?.FullName == "System.MulticastDelegate"; }
        catch (Exception ex) when (ex is IOException or TypeLoadException) { return false; }
    }

    private static Type? SafeBase(Type type)
    {
        try { return type.BaseType; }
        catch (Exception ex) when (ex is IOException or TypeLoadException) { return null; }
    }

    private static bool HasAttribute(ICustomAttributeProvider member, string name)
    {
        try
        {
            var data = member switch
            {
                MemberInfo m => m.CustomAttributes,
                ParameterInfo p => p.CustomAttributes,
                _ => []
            };
            return data.Any(a => a.AttributeType.Name == name);
        }
        catch (Exception ex) when (ex is IOException or TypeLoadException or NotSupportedException) { return false; }
    }

    /// <summary>One member's line, or nothing when a type it uses can't be resolved.</summary>
    private static string Try(Func<string> render)
    {
        try { return render(); }
        catch (Exception ex) when (ex is IOException or TypeLoadException or NotSupportedException or InvalidOperationException or ArgumentException) { return ""; }
    }
}

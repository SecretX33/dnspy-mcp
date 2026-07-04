using System.Collections.Concurrent;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

namespace DnSpyMcpServer.Services;

internal enum OpcodeEditMode
{
    Overwrite,
    Append
}

internal sealed class AssemblyAnalysisService
{
    private readonly ConcurrentDictionary<string, LoadedAssembly> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetCachedAssemblyPaths() => _cache.Keys.OrderBy(x => x).ToArray();

    public string GetAssemblySummary(string assemblyPath)
    {
        var asm = GetOrLoad(assemblyPath);
        var module = asm.Module;
        var allTypes = module.GetTypes().Where(t => !t.IsGlobalModuleType).ToArray();
        var methodCount = allTypes.Sum(t => t.Methods.Count);

        var lines = new[]
        {
            $"path: {asm.Path}",
            $"module: {module.Name}",
            $"runtime: {module.RuntimeVersion}",
            $"types: {allTypes.Length}",
            $"methods: {methodCount}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    public string[] GetTypes(string assemblyPath, string? namespaceFilter, bool includeNested)
    {
        var module = GetOrLoad(assemblyPath).Module;
        return module.GetTypes()
            .Where(t => !t.IsGlobalModuleType)
            .Where(t => includeNested || !t.IsNested)
            .Where(t => string.IsNullOrWhiteSpace(namespaceFilter) || string.Equals(t.Namespace, namespaceFilter, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .OrderBy(t => t)
            .ToArray();
    }

    public string ListTypes(string assemblyPath, string? namespaceFilter, bool includeNested)
    {
        var types = GetTypes(assemblyPath, namespaceFilter, includeNested);
        return types.Length == 0 ? "No types found." : string.Join(Environment.NewLine, types);
    }

    public string DecompileType(string assemblyPath, string typeFullName)
    {
        var asm = GetOrLoad(assemblyPath);
        var type = FindType(asm.Module, typeFullName);
        var code = asm.Decompiler.DecompileTypeAsString(new FullTypeName(type.ReflectionFullName));

        return $"// dnSpy location: TypeDef token {FormatToken(type.MDToken.Raw)}{Environment.NewLine}{code}";
    }

    public string DecompileMethod(string assemblyPath, string typeFullName, string methodName, string[]? parameterTypeNames)
    {
        var asm = GetOrLoad(assemblyPath);
        var type = FindType(asm.Module, typeFullName);
        var method = FindMethod(type, methodName, parameterTypeNames);

        var handle = MetadataTokens.EntityHandle((int)method.MDToken.Raw);
        var code = asm.Decompiler.DecompileAsString(new[] { handle });

        return $"// dnSpy location: TypeDef {FormatToken(type.MDToken.Raw)}, MethodDef {FormatToken(method.MDToken.Raw)}{Environment.NewLine}{code}";
    }

    public string GetMethodIl(string assemblyPath, string typeFullName, string methodName, string[]? parameterTypeNames)
    {
        var module = GetOrLoad(assemblyPath).Module;
        var type = FindType(module, typeFullName);
        var method = FindMethod(type, methodName, parameterTypeNames);

        if (!method.HasBody || method.Body is null)
            return "Method has no IL body.";

        var sb = new StringBuilder();
        sb.AppendLine($"dnSpy location: TypeDef {FormatToken(type.MDToken.Raw)}, MethodDef {FormatToken(method.MDToken.Raw)}");
        sb.AppendLine(RenderMethodSignature(method));
        foreach (var instruction in method.Body.Instructions)
        {
            var operand = instruction.Operand is null ? string.Empty : $" {instruction.Operand}";
            sb.AppendLine($"IL_{instruction.Offset:X4}: {instruction.OpCode}{operand}");
        }

        return sb.ToString();
    }

    public string SearchMembers(string assemblyPath, string query, int maxResults)
    {
        if (maxResults <= 0)
            maxResults = 500;

        var module = GetOrLoad(assemblyPath).Module;
        var results = new List<string>(capacity: Math.Min(maxResults, 1000));

        foreach (var type in module.GetTypes().Where(t => !t.IsGlobalModuleType))
        {
            if (ContainsIgnoreCase(type.FullName, query))
                results.Add($"type: {type.FullName} | TypeDef={FormatToken(type.MDToken.Raw)}");

            foreach (var method in type.Methods)
            {
                if (ContainsIgnoreCase(method.Name, query)) 
                {
                    var methodLine = $"method: {RenderMethodSignature(method)} | Type={type.FullName} | TypeDef={FormatToken(type.MDToken.Raw)} | MethodDef={FormatToken(method.MDToken.Raw)}";
                    var rva = GetMethodAddressRva(method);
                    if (rva != null)
                        methodLine += $" | Il2CppRVA={rva}";
                    results.Add(methodLine);
                }
            }

            foreach (var field in type.Fields)
            {
                if (ContainsIgnoreCase(field.Name, query))
                {
                    var fieldLine = $"field: {field.Name} | Type={type.FullName} | TypeDef={FormatToken(type.MDToken.Raw)} | FieldDef={FormatToken(field.MDToken.Raw)}";
                    var offset = GetFieldOffset(field);
                    if (offset != null)
                        fieldLine += $" | Il2CppFieldOffset={offset}";
                    results.Add(fieldLine);
                }
            }

            foreach (var property in type.Properties)
            {
                if (ContainsIgnoreCase(property.Name, query))
                {
                    results.Add(
                        $"property: {property.Name} | Type={type.FullName} | TypeDef={FormatToken(type.MDToken.Raw)} | PropertyDef={FormatToken(property.MDToken.Raw)}");
                }
            }

            if (results.Count >= maxResults)
                break;
        }

        return results.Count == 0
            ? "No matches found."
            : string.Join(Environment.NewLine, results.Take(maxResults));
    }

    public string FindStringReferences(string assemblyPath, string text, bool caseSensitive, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Search text cannot be empty.");

        if (maxResults <= 0)
            maxResults = 500;

        var module = GetOrLoad(assemblyPath).Module;
        var results = new List<string>(Math.Min(maxResults, 1000));

        foreach (var type in module.GetTypes().Where(t => !t.IsGlobalModuleType))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || method.Body is null)
                    continue;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not string literal)
                        continue;

                    if (!Contains(literal, text, caseSensitive))
                        continue;

                    var match =
                        $"{type.FullName}.{RenderMethodSignature(method)} | TypeDef={FormatToken(type.MDToken.Raw)} | MethodDef={FormatToken(method.MDToken.Raw)} | IL_{instruction.Offset:X4} | \"{literal}\"";
                    results.Add(match);

                    if (results.Count >= maxResults)
                        return string.Join(Environment.NewLine, results);
                }
            }
        }

        return results.Count == 0 ? "No string references found." : string.Join(Environment.NewLine, results);
    }

    public string ListMethods(string assemblyPath, string typeFullName)
    {
        var module = GetOrLoad(assemblyPath).Module;
        var type = FindType(module, typeFullName);

        var methods = type.Methods
            .Where(m => !m.IsGetter && !m.IsSetter && !m.IsAddOn && !m.IsRemoveOn)
            .Select(m => $"{RenderMethodSignature(m)} | MethodDef={FormatToken(m.MDToken.Raw)}")
            .OrderBy(m => m)
            .ToArray();

        return methods.Length == 0
            ? "No methods found."
            : string.Join(Environment.NewLine, methods);
    }

    public string PatchReplaceStringLiteral(string assemblyPath, string methodDefToken, string ilOffset, string newText,
        bool inPlace = false, string? outputPath = null)
    {
        return RunModuleEdit(assemblyPath, inPlace, outputPath, "Patch applied: replace string literal", module =>
        {
            var method = ResolveMethodByToken(module, methodDefToken);
            if (!method.HasBody || method.Body is null)
                throw new InvalidOperationException($"Method has no IL body: {methodDefToken}");

            var offset = ParseIlOffset(ilOffset);
            var instruction = method.Body.Instructions.FirstOrDefault(i => i.Offset == offset)
                ?? throw new InvalidOperationException($"IL offset not found in method {methodDefToken}: IL_{offset:X4}");

            if (instruction.Operand is not string oldText)
                throw new InvalidOperationException($"Instruction at IL_{offset:X4} is not a string literal (ldstr).");

            instruction.Operand = newText;
            return new[]
            {
                $"method: {FormatToken(method.MDToken.Raw)}",
                $"offset: IL_{offset:X4}",
                $"old: \"{oldText}\"",
                $"new: \"{newText}\""
            };
        });
    }

    public string PatchNopInstructions(string assemblyPath, string methodDefToken, string ilOffset, int count,
        bool inPlace = false, string? outputPath = null)
    {
        if (count <= 0)
            throw new InvalidOperationException("count must be > 0");

        try
        {
            return RunModuleEdit(assemblyPath, inPlace, outputPath, "Patch applied: NOP instructions", module =>
            {
                var method = ResolveMethodByToken(module, methodDefToken);
                if (!method.HasBody || method.Body is null)
                    throw new InvalidOperationException($"Method has no IL body: {methodDefToken}");

                var offset = ParseIlOffset(ilOffset);
                var instructions = method.Body.Instructions;
                var startIndex = IndexOfOffset(instructions, offset);
                if (startIndex < 0)
                    throw new InvalidOperationException($"IL offset not found in method {methodDefToken}: IL_{offset:X4}");

                var end = Math.Min(startIndex + count, instructions.Count);
                for (var i = startIndex; i < end; i++)
                    instructions[i] = new Instruction(OpCodes.Nop);

                return new[]
                {
                    $"method: {FormatToken(method.MDToken.Raw)}",
                    $"startOffset: IL_{offset:X4}",
                    $"count: {end - startIndex}"
                };
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Patch NOP failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    public string RenameType(string assemblyPath, string typeFullName, string newName, string? newNamespace,
        bool inPlace = false, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("newName cannot be empty.");

        return RunModuleEdit(assemblyPath, inPlace, outputPath, "Rename applied: type", module =>
        {
            var type = FindType(module, typeFullName);
            var oldFullName = type.FullName;
            type.Name = newName;
            if (newNamespace is not null)
                type.Namespace = newNamespace;

            return new[]
            {
                $"typeDef: {FormatToken(type.MDToken.Raw)}",
                $"old: {oldFullName}",
                $"new: {type.FullName}"
            };
        });
    }

    public string RenameMethod(string assemblyPath, string typeFullName, string methodName, string newName,
        string[]? parameterTypeNames, bool inPlace = false, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new InvalidOperationException("newName cannot be empty.");

        return RunModuleEdit(assemblyPath, inPlace, outputPath, "Rename applied: method", module =>
        {
            var type = FindType(module, typeFullName);
            var method = FindMethod(type, methodName, parameterTypeNames);
            var oldSig = RenderMethodSignature(method);
            method.Name = newName;

            return new[]
            {
                $"typeDef: {FormatToken(type.MDToken.Raw)}",
                $"methodDef: {FormatToken(method.MDToken.Raw)}",
                $"old: {oldSig}",
                $"new: {RenderMethodSignature(method)}"
            };
        });
    }

    public string RenameNamespace(string assemblyPath, string oldNamespace, string newNamespace,
        bool inPlace = false, string? outputPath = null)
    {
        return RunModuleEdit(assemblyPath, inPlace, outputPath, "Rename applied: namespace", module =>
        {
            var affected = module.GetTypes()
                .Where(t => !t.IsGlobalModuleType)
                .Where(t => string.Equals(t.Namespace, oldNamespace, StringComparison.Ordinal))
                .ToArray();

            if (affected.Length == 0)
                throw new InvalidOperationException($"No types found in namespace: '{oldNamespace}'");

            foreach (var type in affected)
                type.Namespace = newNamespace;

            return new[]
            {
                $"oldNamespace: {oldNamespace}",
                $"newNamespace: {newNamespace}",
                $"typesUpdated: {affected.Length}"
            };
        });
    }

    public string SetFunctionOpcodes(string assemblyPath, string typeFullName, string methodName,
        string[]? parameterTypeNames, string[] ilOpcodes, int index, OpcodeEditMode mode,
        bool inPlace = false, string? outputPath = null)
    {
        if (ilOpcodes is null || ilOpcodes.Length == 0)
            throw new InvalidOperationException("ilOpcodes cannot be empty.");
        if (index < 0)
            throw new InvalidOperationException("index must be >= 0.");

        return RunModuleEdit(assemblyPath, inPlace, outputPath, $"IL edit applied ({mode})", module =>
        {
            var type = FindType(module, typeFullName);
            var method = FindMethod(type, methodName, parameterTypeNames);
            if (!method.HasBody || method.Body is null)
                throw new InvalidOperationException($"Method has no IL body: {type.FullName}.{methodName}");

            var instructions = method.Body.Instructions;
            if (index > instructions.Count)
                throw new InvalidOperationException($"index {index} is past the end of the method ({instructions.Count} instructions).");

            var parsed = ParseInstructions(module, ilOpcodes);

            if (mode == OpcodeEditMode.Overwrite)
            {
                var replaceCount = Math.Min(parsed.Length, instructions.Count - index);
                for (var i = 0; i < replaceCount; i++)
                    instructions[index + i] = parsed[i];
                for (var i = replaceCount; i < parsed.Length; i++)
                    instructions.Insert(index + i, parsed[i]);
            }
            else
            {
                for (var i = 0; i < parsed.Length; i++)
                    instructions.Insert(index + i, parsed[i]);
            }

            EnsureBranchTargetsPresent(instructions);
            method.Body.UpdateInstructionOffsets();
            method.Body.KeepOldMaxStack = false;

            return new[]
            {
                $"typeDef: {FormatToken(type.MDToken.Raw)}",
                $"methodDef: {FormatToken(method.MDToken.Raw)}",
                $"mode: {mode}",
                $"index: {index}",
                $"instructionsWritten: {parsed.Length}",
                "-- new body --"
            }.Concat(RenderInstructions(instructions)).ToArray();
        });
    }

    public string OverwriteMethodBody(string assemblyPath, string typeFullName, string methodName,
        string[]? parameterTypeNames, string[] ilOpcodes, bool inPlace = false, string? outputPath = null)
    {
        if (ilOpcodes is null || ilOpcodes.Length == 0)
            throw new InvalidOperationException("ilOpcodes cannot be empty.");

        return RunModuleEdit(assemblyPath, inPlace, outputPath, "IL body overwritten", module =>
        {
            var type = FindType(module, typeFullName);
            var method = FindMethod(type, methodName, parameterTypeNames);
            method.Body ??= new CilBody();

            var parsed = ParseInstructions(module, ilOpcodes);

            method.Body.Instructions.Clear();
            method.Body.ExceptionHandlers.Clear();
            foreach (var instruction in parsed)
                method.Body.Instructions.Add(instruction);

            method.Body.UpdateInstructionOffsets();
            method.Body.KeepOldMaxStack = false;

            return new[]
            {
                $"typeDef: {FormatToken(type.MDToken.Raw)}",
                $"methodDef: {FormatToken(method.MDToken.Raw)}",
                $"instructionsWritten: {parsed.Length}",
                "-- new body --"
            }.Concat(RenderInstructions(method.Body.Instructions)).ToArray();
        });
    }

    private const string PatchTypeName = "__DnSpyMcpPatch";

    public string UpdateMethodSource(string assemblyPath, string typeFullName, string methodName,
        string[]? parameterTypeNames, string source, bool inPlace = false, string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("source cannot be empty.");

        var referenceRoot = NormalizePath(assemblyPath);

        return RunModuleEdit(assemblyPath, inPlace, outputPath, "Method source recompiled", module =>
        {
            var type = FindType(module, typeFullName);
            var target = FindMethod(type, methodName, parameterTypeNames);

            var compiled = CompileMethod(referenceRoot, type, source, out var subclassed, out var diagnostics);
            if (compiled is null)
                throw new InvalidOperationException("Compilation failed:" + Environment.NewLine + diagnostics);

            using var patchModule = ModuleDefMD.Load(compiled);
            var patchType = patchModule.Types.FirstOrDefault(t => t.Name == PatchTypeName)
                ?? throw new InvalidOperationException("Internal error: compiled patch type not found.");
            var patchMethod = patchType.Methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.Ordinal) && m.HasBody)
                ?? throw new InvalidOperationException($"The supplied source must declare a method named '{methodName}'.");

            if (patchMethod.IsStatic != target.IsStatic)
                throw new InvalidOperationException($"Static/instance mismatch: target {(target.IsStatic ? "is" : "is not")} static, supplied method {(patchMethod.IsStatic ? "is" : "is not")}.");
            if (patchMethod.MethodSig.Params.Count != target.MethodSig.Params.Count)
                throw new InvalidOperationException($"Parameter count mismatch: target has {target.MethodSig.Params.Count}, supplied method has {patchMethod.MethodSig.Params.Count}.");
            // TypeSig.FullName carries no assembly scope, so this compares by namespace+name and stays correct even
            // when a parameter or return type is one of the target module's own types.
            if (!string.Equals(patchMethod.MethodSig.RetType.FullName, target.MethodSig.RetType.FullName, StringComparison.Ordinal))
                throw new InvalidOperationException($"Return type mismatch: target returns '{target.MethodSig.RetType.FullName}', supplied method returns '{patchMethod.MethodSig.RetType.FullName}'.");
            for (var i = 0; i < target.MethodSig.Params.Count; i++)
            {
                if (!string.Equals(patchMethod.MethodSig.Params[i].FullName, target.MethodSig.Params[i].FullName, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Parameter {i} type mismatch: target expects '{target.MethodSig.Params[i].FullName}', supplied method has '{patchMethod.MethodSig.Params[i].FullName}'.");
            }

            CopyMethodBody(module, patchMethod, target);

            return new[]
            {
                $"typeDef: {FormatToken(type.MDToken.Raw)}",
                $"methodDef: {FormatToken(target.MDToken.Raw)}",
                $"access: {(subclassed ? "subclassed target (this and the target's own private members are available)" : "standalone wrapper (no this or private-member access; target is a value type, delegate, static, generic, or nested type, or has no satisfiable constructor)")}",
                $"localsCopied: {target.Body!.Variables.Count}",
                $"instructionsCopied: {target.Body.Instructions.Count}",
                "-- new body --"
            }.Concat(RenderInstructions(target.Body.Instructions)).ToArray();
        });
    }

    private static byte[]? CompileMethod(string referenceRoot, TypeDef targetType, string source, out bool subclassed, out string diagnostics)
    {
        var wrapper = BuildWrapperSource(targetType, source, out subclassed);

        byte[]? publicizedTarget = null;
        if (subclassed)
        {
            // Only needed when the wrapper subclasses the target; on failure fall back to the on-disk reference,
            // which still permits public/protected members via `this`.
            try { publicizedTarget = BuildPublicizedImage(referenceRoot); }
            catch { publicizedTarget = null; }
        }

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(wrapper);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            assemblyName: "__DnSpyMcpPatchAsm",
            syntaxTrees: new[] { tree },
            references: GatherReferences(referenceRoot, publicizedTarget, targetType.Module.Assembly?.Name?.String),
            options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            diagnostics = string.Join(Environment.NewLine, result.Diagnostics
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return null;
        }

        diagnostics = string.Empty;
        return ms.ToArray();
    }

    private const string WrapperUsings =
        "using System;\n" +
        "using System.Collections;\n" +
        "using System.Collections.Generic;\n" +
        "using System.Globalization;\n" +
        "using System.Linq;\n" +
        "using System.Text;\n\n";

    // Compiles the supplied method inside a wrapper type. When the target can be subclassed, the wrapper derives from
    // it so `this` is the target type and the body can use the target's own members (including private ones, made
    // reachable by compiling against a publicized copy of the target); those member references graft back to local
    // definitions. Otherwise it falls back to a standalone wrapper with no access to the target's instance members.
    private static string BuildWrapperSource(TypeDef targetType, string source, out bool subclassed)
    {
        if (CanSubclass(targetType, out var ctorMember))
        {
            subclassed = true;
            return WrapperUsings +
                $"public abstract class {PatchTypeName} : {targetType.FullName}\n" +
                "{\n" +
                ctorMember +
                source + "\n" +
                "}\n";
        }

        subclassed = false;
        return WrapperUsings +
            $"public class {PatchTypeName}\n" +
            "{\n" +
            source + "\n" +
            "}\n";
    }

    // Decides whether the wrapper can derive from the target type. Value types, enums, delegates and static classes are
    // sealed and fall out here; interfaces, generic and nested targets are left for the standalone fallback. Emits an
    // explicit base-constructor call (via the target-typed `default` literal) only when there is no parameterless ctor
    // and exactly one shortest-arity ctor to bind unambiguously.
    private static bool CanSubclass(TypeDef targetType, out string ctorMember)
    {
        ctorMember = string.Empty;

        // Sealed reference classes are fine: the publicized copy is unsealed for the compile. Exclude the kinds that
        // genuinely cannot serve as a base class: value types, delegates, and static (abstract+sealed) classes, plus
        // interfaces, generic and nested targets (out of scope), and targets with no assembly identity.
        if (targetType.IsInterface || targetType.IsValueType
            || string.Equals(targetType.BaseType?.FullName, "System.MulticastDelegate", StringComparison.Ordinal)
            || (targetType.IsAbstract && targetType.IsSealed)
            || targetType.HasGenericParameters || targetType.DeclaringType is not null
            || targetType.Module.Assembly?.Name is null)
            return false;

        var instanceCtors = targetType.Methods
            .Where(m => m.IsConstructor && !m.IsStatic && m.MethodSig is not null)
            .ToArray();
        if (instanceCtors.Length == 0)
            return false;

        if (instanceCtors.Any(c => c.MethodSig.Params.Count == 0))
            return true; // implicit base() binds; the publicized copy makes a non-public parameterless ctor callable

        var minArity = instanceCtors.Min(c => c.MethodSig.Params.Count);
        var shortest = instanceCtors.Where(c => c.MethodSig.Params.Count == minArity).ToArray();
        if (shortest.Length != 1)
            return false; // ambiguous base(default, ...) — fall back rather than risk binding the wrong ctor

        ctorMember = $"    private {PatchTypeName}() : base({string.Join(", ", Enumerable.Repeat("default", minArity))}) {{ }}\n";
        return true;
    }

    private static List<Microsoft.CodeAnalysis.MetadataReference> GatherReferences(string referenceRoot, byte[]? publicizedTarget, string? targetSimpleName)
    {
        var references = new List<Microsoft.CodeAnalysis.MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Host runtime reference set (this process runs on the same net8.0 shared framework).
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (path.Length > 0 && File.Exists(path) && seen.Add(Path.GetFileName(path)))
                    references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
            }
        }

        // The publicized image is the single reference that carries the target's identity, so the body can reach the
        // target's private members. Any other on-disk file that shares that identity (e.g. a prior *.patched copy) is
        // skipped below; two references with the same identity would let Roslyn bind against the non-publicized one.
        if (publicizedTarget is not null)
            references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(publicizedTarget));

        // The target's neighbours, so the source can call into the app's own types. Framework assemblies already
        // provided by the host win (deduped by file name) to avoid clashes.
        var directory = Path.GetDirectoryName(referenceRoot);
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll").Concat(Directory.EnumerateFiles(directory, "*.exe")))
            {
                if (!seen.Add(Path.GetFileName(file)))
                    continue;
                if (publicizedTarget is not null && SharesIdentity(file, targetSimpleName))
                    continue;
                try { references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(file)); }
                catch { /* skip native/unreadable files */ }
            }
        }

        return references;
    }

    private static bool SharesIdentity(string file, string? targetSimpleName)
    {
        if (targetSimpleName is null)
            return false;
        try { return string.Equals(System.Reflection.AssemblyName.GetAssemblyName(file).Name, targetSimpleName, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // A compile-against copy of the target with every type and member made public (and reference types unsealed), so the
    // subclass wrapper can derive from the target and reference its own private members (Roslyn does not honor
    // IgnoresAccessChecksTo at compile time). The emitted references bind by name to the real, still-private members
    // during grafting, and because the grafted method lives inside the target type, the access is legal at runtime.
    private static byte[] BuildPublicizedImage(string assemblyPath)
    {
        using var module = ModuleDefMD.Load(File.ReadAllBytes(assemblyPath));
        foreach (var type in module.GetTypes())
        {
            var attributes = (type.Attributes & ~TypeAttributes.VisibilityMask)
                | (type.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public);
            // Unseal reference types so the wrapper can subclass a sealed target at compile time; the shipped binary is
            // untouched. Value types stay sealed since C# forbids deriving from them anyway.
            if (!type.IsValueType)
                attributes &= ~TypeAttributes.Sealed;
            type.Attributes = attributes;
            foreach (var method in type.Methods)
                method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
            foreach (var field in type.Fields)
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
        }

        using var ms = new MemoryStream();
        module.Write(ms);
        return ms.ToArray();
    }

    // Clones a compiled method body into the target method, importing every type/method/field reference into the
    // target module and remapping locals, parameters, branch targets and exception handlers.
    private static void CopyMethodBody(ModuleDefMD targetModule, MethodDef source, MethodDef target)
    {
        var importer = new Importer(targetModule,
            ImporterOptions.TryToUseTypeDefs | ImporterOptions.TryToUseMethodDefs | ImporterOptions.TryToUseFieldDefs);

        var body = new CilBody { InitLocals = source.Body.InitLocals };

        var localMap = new Dictionary<Local, Local>();
        foreach (var local in source.Body.Variables)
        {
            var clonedLocal = new Local(importer.Import(local.Type));
            localMap[local] = clonedLocal;
            body.Variables.Add(clonedLocal);
        }

        var instructionMap = new Dictionary<Instruction, Instruction>();
        foreach (var instruction in source.Body.Instructions)
        {
            var clone = new Instruction(instruction.OpCode) { Operand = instruction.Operand };
            instructionMap[instruction] = clone;
            body.Instructions.Add(clone);
        }

        foreach (var instruction in body.Instructions)
        {
            switch (instruction.OpCode.OperandType)
            {
                case OperandType.InlineType:
                    instruction.Operand = ImportType(targetModule, importer, (ITypeDefOrRef)instruction.Operand);
                    break;
                case OperandType.InlineMethod:
                    instruction.Operand = ImportMethod(targetModule, importer, (dnlib.DotNet.IMethod)instruction.Operand);
                    break;
                case OperandType.InlineField:
                    instruction.Operand = ImportField(targetModule, importer, (dnlib.DotNet.IField)instruction.Operand);
                    break;
                case OperandType.InlineTok:
                    instruction.Operand = ImportTokenOperand(targetModule, importer, instruction.Operand);
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    instruction.Operand = instructionMap[(Instruction)instruction.Operand];
                    break;
                case OperandType.InlineSwitch:
                    instruction.Operand = ((Instruction[])instruction.Operand).Select(i => instructionMap[i]).ToArray();
                    break;
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    if (instruction.Operand is Local local)
                        instruction.Operand = localMap[local];
                    else if (instruction.Operand is Parameter parameter)
                        instruction.Operand = target.Parameters[parameter.Index];
                    break;
            }
        }

        foreach (var handler in source.Body.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                CatchType = handler.CatchType is null ? null : importer.Import(handler.CatchType),
                TryStart = MapInstruction(instructionMap, handler.TryStart),
                TryEnd = MapInstruction(instructionMap, handler.TryEnd),
                HandlerStart = MapInstruction(instructionMap, handler.HandlerStart),
                HandlerEnd = MapInstruction(instructionMap, handler.HandlerEnd),
                FilterStart = MapInstruction(instructionMap, handler.FilterStart)
            });
        }

        body.UpdateInstructionOffsets();
        body.KeepOldMaxStack = false;
        target.Body = body;
    }

    private static Instruction? MapInstruction(IReadOnlyDictionary<Instruction, Instruction> map, Instruction? instruction)
        => instruction is null ? null : map[instruction];

    private static object ImportTokenOperand(ModuleDefMD targetModule, Importer importer, object operand) => operand switch
    {
        ITypeDefOrRef type => ImportType(targetModule, importer, type),
        MemberRef member => member.IsFieldRef
            ? ImportField(targetModule, importer, member)
            : ImportMethod(targetModule, importer, member),
        FieldDef field => ImportField(targetModule, importer, field),
        MethodSpec spec => ImportMethod(targetModule, importer, spec),
        MethodDef method => ImportMethod(targetModule, importer, method),
        _ => operand
    };

    // A reference emitted by the throwaway compiled assembly may point back at a type in the module we are
    // patching. Resolve those to the module's own definitions; only genuinely external (e.g. BCL) references
    // go through the importer, which would otherwise leave a dangling ref to the compiled patch assembly.
    private static ITypeDefOrRef ImportType(ModuleDefMD targetModule, Importer importer, ITypeDefOrRef type)
    {
        var local = targetModule.Find(type.FullName, false);
        if (local is not null)
            return local;

        // A generic instantiation or array/byref over one of the target's own types is a TypeSpec that never
        // name-matches a TypeDef, so importing it would recreate a dangling self-reference. That is outside the
        // supported imperative-body scope, so fail loudly instead of writing a corrupt ref. Pure BCL composites
        // (e.g. List<int>) reference no local type and import cleanly.
        if (type is TypeSpec spec && ReferencesLocalType(targetModule, spec.TypeSig))
            throw new InvalidOperationException(
                $"Cannot safely graft the composed type '{type.FullName}': it is built over one of the target module's " +
                "own types, which is outside the supported imperative-body scope. Rewrite the body to avoid it, or use " +
                "set_function_opcodes/overwrite_method_body.");

        return importer.Import(type);
    }

    private static bool ReferencesLocalType(ModuleDefMD targetModule, TypeSig? sig)
    {
        while (sig is not null)
        {
            switch (sig)
            {
                case GenericInstSig git:
                    if (ReferencesLocalType(targetModule, git.GenericType))
                        return true;
                    return git.GenericArguments.Any(arg => ReferencesLocalType(targetModule, arg));
                case TypeDefOrRefSig leaf:
                    return targetModule.Find(leaf.TypeDefOrRef.FullName, false) is not null;
                default:
                    sig = sig.Next;
                    break;
            }
        }

        return false;
    }

    private static dnlib.DotNet.IMethod ImportMethod(ModuleDefMD targetModule, Importer importer, dnlib.DotNet.IMethod method)
    {
        var localType = method.DeclaringType is null ? null : targetModule.Find(method.DeclaringType.FullName, false);
        var local = localType is null ? null : FindMatchingMethod(localType, method);
        return local ?? importer.Import(method);
    }

    private static dnlib.DotNet.IField ImportField(ModuleDefMD targetModule, Importer importer, dnlib.DotNet.IField field)
    {
        var localType = field.DeclaringType is null ? null : targetModule.Find(field.DeclaringType.FullName, false);
        var local = localType?.Fields.FirstOrDefault(f => string.Equals(f.Name, field.Name, StringComparison.Ordinal));
        return local ?? importer.Import(field);
    }

    private static MethodDef? FindMatchingMethod(TypeDef type, dnlib.DotNet.IMethod method)
    {
        var signature = method.MethodSig;
        var sameName = type.Methods
            .Where(m => string.Equals(m.Name, method.Name, StringComparison.Ordinal))
            .ToArray();

        if (sameName.Length == 0)
            return null;

        if (signature is not null)
        {
            var exact = sameName.FirstOrDefault(candidate =>
                candidate.MethodSig.Params.Count == signature.Params.Count &&
                Enumerable.Range(0, signature.Params.Count).All(i =>
                    string.Equals(candidate.MethodSig.Params[i].FullName, signature.Params[i].FullName, StringComparison.Ordinal)));
            if (exact is not null)
                return exact;
        }

        // Either there was no signature to match on, or none of the overloads lined up. Bind by name only when it is
        // unambiguous; refuse to guess between overloads rather than silently splice a call to the wrong one.
        if (sameName.Length == 1)
            return sameName[0];

        throw new InvalidOperationException(
            $"Cannot resolve a call to '{type.FullName}.{method.Name}': multiple overloads exist and none matches the " +
            "compiled call's signature (a local overloaded/generic call is outside the supported imperative-body scope).");
    }

    // Assembles opcode strings ("ldstr \"hi\"", "call System.Console::WriteLine(System.String)", "br 4") into
    // dnlib instructions. Supported operands: none, string literal, ldc numeric, call/callvirt/newobj (methods),
    // ld/st fields, type operands, and branch targets addressed by 0-based instruction index. Anything else throws.
    private static Instruction[] ParseInstructions(ModuleDefMD module, string[] ilOpcodes)
    {
        var importer = new Importer(module);
        var result = new List<Instruction>();
        var branchTargets = new List<(int InstructionIndex, int TargetIndex)>();

        foreach (var raw in ilOpcodes)
        {
            var line = raw?.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var split = SplitMnemonic(line);
            var mnemonic = split.Mnemonic;
            var operandText = split.Operand;

            if (!OpcodeMap.TryGetValue(mnemonic, out var opcode))
                throw new InvalidOperationException($"Unknown IL opcode '{mnemonic}' in line: {raw}");

            var instruction = opcode.OperandType switch
            {
                OperandType.InlineNone => Instruction.Create(opcode),
                OperandType.InlineString => Instruction.Create(opcode, ParseStringOperand(operandText)),
                OperandType.ShortInlineI => Instruction.Create(opcode, ParseSByte(operandText)),
                OperandType.InlineI => Instruction.Create(opcode, ParseInt(operandText)),
                OperandType.InlineI8 => Instruction.Create(opcode, ParseLong(operandText)),
                OperandType.ShortInlineR => Instruction.Create(opcode, ParseFloat(operandText)),
                OperandType.InlineR => Instruction.Create(opcode, ParseDouble(operandText)),
                OperandType.InlineMethod => Instruction.Create(opcode, ResolveMethodOperand(module, importer, operandText)),
                OperandType.InlineField => Instruction.Create(opcode, ResolveFieldOperand(module, importer, operandText)),
                OperandType.InlineType => Instruction.Create(opcode, ResolveTypeOperand(module, importer, operandText)),
                OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget => new Instruction(opcode),
                _ => throw new InvalidOperationException(
                    $"Unsupported operand type '{opcode.OperandType}' for opcode '{mnemonic}'. Supported: no-operand opcodes, ldstr, ldc.i4/i8/r4/r8, call/callvirt/newobj, ld/st fields, type operands, and branch targets (by instruction index).")
            };

            if (opcode.OperandType is OperandType.InlineBrTarget or OperandType.ShortInlineBrTarget)
                branchTargets.Add((result.Count, ParseInt(operandText)));

            result.Add(instruction);
        }

        foreach (var (instructionIndex, targetIndex) in branchTargets)
        {
            if (targetIndex < 0 || targetIndex >= result.Count)
                throw new InvalidOperationException($"Branch target index {targetIndex} is out of range (valid: 0..{result.Count - 1}).");
            result[instructionIndex].Operand = result[targetIndex];
        }

        return result.ToArray();
    }

    private static (string Mnemonic, string Operand) SplitMnemonic(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i]))
                return (line[..i], line[(i + 1)..].Trim());
        }

        return (line, string.Empty);
    }

    private static ITypeDefOrRef ResolveTypeOperand(ModuleDefMD module, Importer importer, string typeName)
    {
        typeName = typeName.Trim();
        var typeDef = module.Find(typeName, false) ?? module.Find(typeName, true);
        if (typeDef is not null)
            return typeDef;

        var reflectionType = ResolveClrType(typeName);
        if (reflectionType is not null)
            return importer.Import(reflectionType);

        throw new InvalidOperationException($"Could not resolve type operand '{typeName}'. Use a type defined in the target module or a resolvable BCL type (e.g. System.String).");
    }

    private static dnlib.DotNet.IMethod ResolveMethodOperand(ModuleDefMD module, Importer importer, string operandText)
    {
        var (typeName, memberName, parameterTypes) = SplitMemberReference(operandText, "method");

        var typeDef = module.Find(typeName, false) ?? module.Find(typeName, true);
        if (typeDef is not null)
        {
            var method = typeDef.Methods.FirstOrDefault(m =>
                string.Equals(m.Name, memberName, StringComparison.Ordinal) &&
                (parameterTypes is null || SignatureMatches(m, parameterTypes)));
            if (method is not null)
                return method;
        }

        var reflectionType = ResolveClrType(typeName);
        if (reflectionType is not null)
        {
            var method = FindReflectionMethod(reflectionType, memberName, parameterTypes);
            if (method is not null)
                return importer.Import(method);
        }

        throw new InvalidOperationException($"Could not resolve method operand '{operandText}'. Expected 'Type::Method' or 'Type::Method(ParamType, ...)'.");
    }

    private static dnlib.DotNet.IField ResolveFieldOperand(ModuleDefMD module, Importer importer, string operandText)
    {
        var (typeName, memberName, _) = SplitMemberReference(operandText, "field");

        var typeDef = module.Find(typeName, false) ?? module.Find(typeName, true);
        if (typeDef is not null)
        {
            var field = typeDef.Fields.FirstOrDefault(f => string.Equals(f.Name, memberName, StringComparison.Ordinal));
            if (field is not null)
                return field;
        }

        var reflectionType = ResolveClrType(typeName);
        if (reflectionType is not null)
        {
            var field = reflectionType.GetField(memberName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            if (field is not null)
                return importer.Import(field);
        }

        throw new InvalidOperationException($"Could not resolve field operand '{operandText}'. Expected 'Type::Field'.");
    }

    // Resolves an external (non-target-module) type by full name against the host runtime. Type.GetType alone only
    // sees System.Private.CoreLib and this assembly, so framework types like System.Console fail; loading the
    // mscorlib/netstandard facades and going through their type-forwarder tables resolves them to the real assembly.
    private static Type? ResolveClrType(string typeName)
    {
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is not null)
            return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName, throwOnError: false);
            if (type is not null)
                return type;
        }

        foreach (var facade in new[] { "mscorlib", "netstandard", "System.Runtime" })
        {
            try
            {
                type = System.Reflection.Assembly.Load(facade).GetType(typeName, throwOnError: false);
                if (type is not null)
                    return type;
            }
            catch
            {
                // facade not present on this runtime; try the next one
            }
        }

        return null;
    }

    private static (string TypeName, string MemberName, string[]? ParameterTypes) SplitMemberReference(string operandText, string kind)
    {
        operandText = operandText.Trim();
        var separator = operandText.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
            throw new InvalidOperationException($"The {kind} operand must be 'Type::{char.ToUpperInvariant(kind[0])}{kind[1..]}': '{operandText}'");

        var left = operandText[..separator].Trim();
        var right = operandText[(separator + 2)..].Trim();

        // Drop an optional leading return type: "System.Void System.Console::WriteLine".
        var typeName = left.Contains(' ') ? left[(left.LastIndexOf(' ') + 1)..] : left;

        string[]? parameterTypes = null;
        var paren = right.IndexOf('(');
        if (paren >= 0)
        {
            var inside = right[(paren + 1)..].TrimEnd(')').Trim();
            parameterTypes = inside.Length == 0
                ? Array.Empty<string>()
                : inside.Split(',').Select(s => NormalizeTypeName(s.Trim())).ToArray();
            right = right[..paren].Trim();
        }

        return (typeName, right, parameterTypes);
    }

    private static bool SignatureMatches(MethodDef method, string[] parameterTypes)
    {
        var sigParams = method.MethodSig?.Params;
        if (sigParams is null || sigParams.Count != parameterTypes.Length)
            return false;

        for (var i = 0; i < sigParams.Count; i++)
        {
            if (!string.Equals(NormalizeTypeName(sigParams[i].FullName), parameterTypes[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static System.Reflection.MethodBase? FindReflectionMethod(Type type, string methodName, string[]? parameterTypes)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                     System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;

        IEnumerable<System.Reflection.MethodBase> candidates = string.Equals(methodName, ".ctor", StringComparison.Ordinal)
            ? type.GetConstructors(flags)
            : type.GetMethods(flags).Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));

        var list = candidates.ToArray();
        if (parameterTypes is not null)
            list = list.Where(m => m.GetParameters().Length == parameterTypes.Length &&
                                   m.GetParameters().Select(p => NormalizeTypeName(p.ParameterType.FullName ?? p.ParameterType.Name))
                                    .SequenceEqual(parameterTypes, StringComparer.OrdinalIgnoreCase)).ToArray();

        return list.Length == 1 ? list[0] : list.FirstOrDefault();
    }

    private static string ParseStringOperand(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => next
                });
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }

    private static sbyte ParseSByte(string text) =>
        sbyte.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new InvalidOperationException($"Expected an sbyte operand, got '{text}'.");

    private static int ParseInt(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new InvalidOperationException($"Expected an int operand, got '{text}'.");

    private static long ParseLong(string text) =>
        long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new InvalidOperationException($"Expected a long operand, got '{text}'.");

    private static float ParseFloat(string text) =>
        float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new InvalidOperationException($"Expected a float operand, got '{text}'.");

    private static double ParseDouble(string text) =>
        double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new InvalidOperationException($"Expected a double operand, got '{text}'.");

    private static IEnumerable<string> RenderInstructions(IEnumerable<Instruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            var operand = instruction.Operand is null ? string.Empty : $" {instruction.Operand}";
            yield return $"IL_{instruction.Offset:X4}: {instruction.OpCode}{operand}";
        }
    }

    private static readonly Dictionary<string, OpCode> OpcodeMap = BuildOpcodeMap();

    private static Dictionary<string, OpCode> BuildOpcodeMap()
    {
        var map = new Dictionary<string, OpCode>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;
            if (field.GetValue(null) is OpCode opcode && !string.IsNullOrEmpty(opcode.Name))
                map[opcode.Name] = opcode;
        }

        return map;
    }

    private LoadedAssembly GetOrLoad(string assemblyPath)
    {
        var normalized = NormalizePath(assemblyPath);
        if (!File.Exists(normalized))
            throw new FileNotFoundException($"Assembly not found: {normalized}");

        return _cache.GetOrAdd(normalized, static path =>
        {
            var module = ModuleDefMD.Load(path);
            var settings = new DecompilerSettings(LanguageVersion.Latest)
            {
                ThrowOnAssemblyResolveErrors = false
            };

            // Load the PE image fully into memory so the decompiler retains no OS file handle; otherwise a later
            // in-place write to the same file in this session would hit a sharing violation. Disposed on invalidation.
            var peFile = new ICSharpCode.Decompiler.Metadata.PEFile(
                path,
                new MemoryStream(File.ReadAllBytes(path)),
                System.Reflection.PortableExecutable.PEStreamOptions.PrefetchEntireImage);
            var resolver = new ICSharpCode.Decompiler.Metadata.UniversalAssemblyResolver(
                path, throwOnError: false,
                ICSharpCode.Decompiler.Metadata.DotNetCorePathFinderExtensions.DetectTargetFrameworkId(peFile));
            var decompiler = new CSharpDecompiler(peFile, resolver, settings);
            return new LoadedAssembly(path, module, peFile, decompiler);
        });
    }

    private void InvalidateCache(string assemblyPath)
    {
        var normalized = NormalizePath(assemblyPath);
        if (_cache.TryRemove(normalized, out var entry))
        {
            entry.Module.Dispose();
            entry.PeFile.Dispose();
        }
    }

    // Shared write path for every mutating tool: always back up first, load the module from an in-memory
    // copy (so an in-place write never fights a file lock), run the edit, then write to the destination.
    private string RunModuleEdit(string assemblyPath, bool inPlace, string? outputPath, string title,
        Func<ModuleDefMD, IEnumerable<string>> mutate)
    {
        var sourcePath = NormalizePath(assemblyPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Assembly not found: {sourcePath}");

        var destinationPath = ResolvePatchDestination(sourcePath, inPlace, outputPath);
        var backupPath = BuildBackupPath(sourcePath);
        File.Copy(sourcePath, backupPath, overwrite: false);

        InvalidateCache(sourcePath);
        if (!string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            InvalidateCache(destinationPath);

        var detail = new List<string>();
        using (var module = ModuleDefMD.Load(File.ReadAllBytes(sourcePath)))
        {
            detail.AddRange(mutate(module));
            module.Write(destinationPath);
        }

        var lines = new List<string>
        {
            title,
            $"source: {sourcePath}",
            $"backup: {backupPath}",
            $"output: {destinationPath}"
        };
        lines.AddRange(detail);
        return string.Join(Environment.NewLine, lines);
    }

    // A partial Overwrite swaps Instruction objects in place; any branch or switch elsewhere in the body that still
    // points at a replaced (now-removed) object would dangle and make module.Write throw. Detect it up front and fail
    // with a clear message rather than emitting a broken assembly. HashSet uses reference identity, which is what we want.
    private static void EnsureBranchTargetsPresent(IList<Instruction> instructions)
    {
        var present = new HashSet<Instruction>(instructions);
        foreach (var instruction in instructions)
        {
            var orphaned = instruction.Operand switch
            {
                Instruction target => !present.Contains(target),
                Instruction[] targets => targets.Any(t => !present.Contains(t)),
                _ => false
            };

            if (orphaned)
                throw new InvalidOperationException(
                    "This overwrite orphans an existing branch/switch target (a jump elsewhere in the method pointed at a " +
                    "replaced instruction). Use overwrite_method_body to rebuild the whole body instead.");
        }
    }

    private static int IndexOfOffset(IList<Instruction> instructions, int offset)
    {
        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Offset == offset)
                return i;
        }

        return -1;
    }

    private static TypeDef FindType(ModuleDefMD module, string typeFullName)
    {
        var type = module.GetTypes().FirstOrDefault(t =>
            string.Equals(t.FullName, typeFullName, StringComparison.Ordinal) ||
            string.Equals(t.ReflectionFullName, typeFullName, StringComparison.Ordinal));

        return type ?? throw new InvalidOperationException($"Type not found: {typeFullName}");
    }

    private static MethodDef FindMethod(TypeDef type, string methodName, string[]? parameterTypeNames)
    {
        var candidates = type.Methods
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException($"Method not found: {type.FullName}.{methodName}");

        if (parameterTypeNames is { Length: > 0 })
        {
            var normalized = parameterTypeNames.Select(NormalizeTypeName).ToArray();
            var matched = candidates.Where(m => ParametersMatch(m, normalized)).ToArray();

            if (matched.Length == 1)
                return matched[0];

            if (matched.Length == 0)
                throw new InvalidOperationException(
                    $"No overload matched parameterTypeNames for {type.FullName}.{methodName}. Available: {string.Join(" | ", candidates.Select(RenderMethodSignature))}");

            throw new InvalidOperationException(
                $"Multiple overloads matched. Provide more specific parameterTypeNames. Matches: {string.Join(" | ", matched.Select(RenderMethodSignature))}");
        }

        if (candidates.Length == 1)
            return candidates[0];

        throw new InvalidOperationException(
            $"Ambiguous method name '{methodName}'. Provide parameterTypeNames. Available: {string.Join(" | ", candidates.Select(RenderMethodSignature))}");
    }

    private static bool ParametersMatch(MethodDef method, IReadOnlyList<string> normalizedParameterTypeNames)
    {
        // Use MethodSig.Params to exclude the hidden 'this' parameter for instance methods
        var sigParams = method.MethodSig.Params;
        if (sigParams.Count != normalizedParameterTypeNames.Count)
            return false;

        for (var i = 0; i < sigParams.Count; i++)
        {
            var actual = NormalizeTypeName(sigParams[i].FullName);
            if (!string.Equals(actual, normalizedParameterTypeNames[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string RenderMethodSignature(MethodDef method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.FullName} {p.Name}"));
        return $"{method.ReturnType.FullName} {method.Name}({parameters})";
    }

    private static string FormatToken(uint raw) => $"0x{raw:X8}";

    private static MethodDef ResolveMethodByToken(ModuleDefMD module, string methodDefToken)
    {
        var token = ParseHexToken(methodDefToken);
        var provider = module.ResolveToken(token) as MethodDef;
        return provider ?? throw new InvalidOperationException($"MethodDef token not found: {FormatToken(token)}");
    }

    private static uint ParseHexToken(string tokenText)
    {
        var t = tokenText.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t[2..];

        if (!uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"Invalid token format: {tokenText}");

        return value;
    }

    private static int ParseIlOffset(string ilOffset)
    {
        var t = ilOffset.Trim();
        if (t.StartsWith("IL_", StringComparison.OrdinalIgnoreCase))
            t = t[3..];

        if (!int.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException($"Invalid IL offset: {ilOffset}");

        return value;
    }

    private static string ResolvePatchDestination(string sourcePath, bool inPlace, string? outputPath)
    {
        if (inPlace)
            return sourcePath;

        if (!string.IsNullOrWhiteSpace(outputPath))
            return NormalizePath(outputPath);

        var dir = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        return Path.Combine(dir, $"{name}.patched{ext}");
    }

    private static string BuildBackupPath(string sourcePath)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var candidate = $"{sourcePath}.{timestamp}.bak";
        var counter = 1;
        while (File.Exists(candidate))
            candidate = $"{sourcePath}.{timestamp}_{counter++}.bak";

        return candidate;
    }

    private static string NormalizeTypeName(string typeName)
        => typeName.Replace(" ", string.Empty, StringComparison.Ordinal)
                   .Replace("+", "/", StringComparison.Ordinal);

    private static bool ContainsIgnoreCase(string source, string value)
        => source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string source, string value, bool caseSensitive)
        => source.Contains(value, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string input)
    {
        if (Path.IsPathRooted(input))
            return Path.GetFullPath(input);

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, input));
    }

    private static string? GetMethodAddressRva(MethodDef method) =>
        GetIl2CppMetadataAttributeValue(method.CustomAttributes, "AddressAttribute", "RVA");

    private static string? GetFieldOffset(FieldDef field) =>
        GetIl2CppMetadataAttributeValue(field.CustomAttributes, "FieldOffset", "Offset");

    private static string? GetIl2CppMetadataAttributeValue(IEnumerable<CustomAttribute> attributes, string attributeName, string argumentName)
    {
        foreach (var attr in attributes)
        {
            if (!attr.TypeFullName.Contains(attributeName))
                continue;
            
            var arg = attr.NamedArguments.FirstOrDefault(a => a.Name == argumentName);
            if (arg == null)
                continue;

            var value = arg.Argument.Value;
            return value switch
            {
                string s => s,
                uint u => $"0x{u:X}",
                ulong ul => $"0x{ul:X}",
                _ => value?.ToString()
            };
        }
        return null;
    }

    private sealed record LoadedAssembly(string Path, ModuleDefMD Module, ICSharpCode.Decompiler.Metadata.PEFile PeFile, CSharpDecompiler Decompiler);
}

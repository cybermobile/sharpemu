// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// Emits the SysAbi export registry at compile time: one static class per assembly whose
/// CreateExports(Generation) lists every [SysAbiExport] handler — generation filtering,
/// name fallback, and library default preserved from the retired reflection scan. NIDs
/// omitted from attributes are derived from the export name with the PS NID algorithm
/// (the same computation the runtime symbol catalog was built from). Handlers written
/// with typed signatures get a SysV register-unmarshalling thunk emitted here.
///
/// Invalid declarations are skipped here and rejected by SysAbiExportAnalyzer as build
/// errors, so nothing can be silently dropped.
/// </summary>
[Generator]
public sealed class SysAbiExportGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = SysAbiExportShape.SysAbiExportAttributeName;

    private sealed class ExportModel : IEquatable<ExportModel>
    {
        public ExportModel(
            string containingType,
            string methodName,
            SysAbiExportShape.HandlerShape shape,
            SysAbiExportShape.ReturnKind returnKind,
            string typedParameterKinds,
            string libraryName,
            string nid,
            string exportName,
            int target)
        {
            ContainingType = containingType;
            MethodName = methodName;
            Shape = shape;
            ReturnKind = returnKind;
            TypedParameterKinds = typedParameterKinds;
            LibraryName = libraryName;
            Nid = nid;
            ExportName = exportName;
            Target = target;
        }

        public string ContainingType { get; }
        public string MethodName { get; }
        public SysAbiExportShape.HandlerShape Shape { get; }
        public SysAbiExportShape.ReturnKind ReturnKind { get; }

        // Deliberately a comma-joined string ("uint,int,cstring:4096") rather than an
        // array: the model must be equatable for incremental-generator caching, and a
        // string gets that for free where an array would need a custom comparer.
        public string TypedParameterKinds { get; }
        public string LibraryName { get; }
        public string Nid { get; }
        public string ExportName { get; }
        public int Target { get; }

        public bool Equals(ExportModel? other) =>
            other is not null &&
            ContainingType == other.ContainingType &&
            MethodName == other.MethodName &&
            Shape == other.Shape &&
            ReturnKind == other.ReturnKind &&
            TypedParameterKinds == other.TypedParameterKinds &&
            LibraryName == other.LibraryName &&
            Nid == other.Nid &&
            ExportName == other.ExportName &&
            Target == other.Target;

        public override bool Equals(object? obj) => Equals(obj as ExportModel);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + ContainingType.GetHashCode();
                hash = (hash * 31) + MethodName.GetHashCode();
                hash = (hash * 31) + Nid.GetHashCode();
                return hash;
            }
        }
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var exports = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) => node is MethodDeclarationSyntax,
                static (attributeContext, _) => CreateModel(attributeContext))
            .Where(static model => model is not null)
            .Collect();

        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? "Assembly");

        context.RegisterSourceOutput(
            exports.Combine(assemblyName),
            static (productionContext, source) => Emit(productionContext, source.Left!, source.Right));
    }

    private static ExportModel? CreateModel(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IMethodSymbol method ||
            !SysAbiExportShape.IsAccessibleFromGeneratedCode(method))
        {
            return null;
        }

        var shape = SysAbiExportShape.Classify(
            method,
            out var typedParameterKinds,
            out _,
            out var returnKind);
        if (shape == SysAbiExportShape.HandlerShape.Invalid)
        {
            return null;
        }

        var attribute = context.Attributes[0];
        var arguments = SysAbiExportShape.ReadArguments(attribute);
        var nid = arguments.Nid;
        var exportName = arguments.ExportName;

        // Mirror ModuleManager.ResolveExportInfo: a missing NID resolves from the export
        // name (algorithmically — equivalent to the runtime catalog lookup, which was
        // built with the same computation); a missing name falls back to the method name.
        if (string.IsNullOrWhiteSpace(nid) && !string.IsNullOrWhiteSpace(exportName))
        {
            nid = Ps5Nid.Compute(exportName);
        }

        if (string.IsNullOrWhiteSpace(nid))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(exportName))
        {
            exportName = method.Name;
        }

        var libraryName = string.IsNullOrWhiteSpace(arguments.LibraryName) ? "libKernel" : arguments.LibraryName;
        return new ExportModel(
            method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            method.Name,
            shape,
            returnKind,
            typedParameterKinds,
            libraryName,
            nid!,
            exportName!,
            arguments.Target);
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<ExportModel?> exports,
        string assemblyName)
    {
        // No exports, no registry: an assembly that merely references the analyzer
        // (e.g. SharpEmu.HLE itself) must not mint a colliding
        // SharpEmu.Generated.SysAbiExportRegistry type.
        if (exports.IsDefaultOrEmpty)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated by SharpEmu.SourceGenerators/SysAbiExportGenerator />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine();
        builder.AppendLine("namespace SharpEmu.Generated;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Compile-time SysAbi export registry for " + assemblyName + ".</summary>");
        builder.AppendLine("public static class SysAbiExportRegistry");
        builder.AppendLine("{");
        builder.AppendLine("    /// <summary>");
        builder.AppendLine("    /// Exports effective for the given registration generation, with the same");
        builder.AppendLine("    /// semantics as the reflection scan: an attribute Target of None inherits the");
        builder.AppendLine("    /// registration generation, and exports outside it are skipped.");
        builder.AppendLine("    /// </summary>");
        builder.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<global::SharpEmu.HLE.ExportedFunction> CreateExports(");
        builder.AppendLine("        global::SharpEmu.HLE.Generation registrationGeneration)");
        builder.AppendLine("    {");
        builder.AppendLine($"        var exports = new global::System.Collections.Generic.List<global::SharpEmu.HLE.ExportedFunction>({exports.Length});");

        foreach (var export in exports)
        {
            if (export is null)
            {
                continue;
            }

            var function = export.Shape switch
            {
                SysAbiExportShape.HandlerShape.ContextOnly
                    when export.ReturnKind == SysAbiExportShape.ReturnKind.Int32 =>
                    $"{export.ContainingType}.{export.MethodName}",
                SysAbiExportShape.HandlerShape.Parameterless
                    when export.ReturnKind == SysAbiExportShape.ReturnKind.Int32 =>
                    $"static _ => {export.ContainingType}.{export.MethodName}()",
                _ => TypedThunk(export),
            };
            builder.AppendLine(
                $"        Add(exports, registrationGeneration, {Literal(export.LibraryName)}, {Literal(export.Nid)}, " +
                $"{Literal(export.ExportName)}, (global::SharpEmu.HLE.Generation){export.Target}, {function});");
        }

        builder.AppendLine("        return exports;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static void Add(");
        builder.AppendLine("        global::System.Collections.Generic.List<global::SharpEmu.HLE.ExportedFunction> exports,");
        builder.AppendLine("        global::SharpEmu.HLE.Generation registrationGeneration,");
        builder.AppendLine("        string libraryName,");
        builder.AppendLine("        string nid,");
        builder.AppendLine("        string exportName,");
        builder.AppendLine("        global::SharpEmu.HLE.Generation attributeTarget,");
        builder.AppendLine("        global::SharpEmu.HLE.SysAbiFunction function)");
        builder.AppendLine("    {");
        builder.AppendLine("        var target = attributeTarget == global::SharpEmu.HLE.Generation.None ? registrationGeneration : attributeTarget;");
        builder.AppendLine("        if ((target & registrationGeneration) == 0)");
        builder.AppendLine("        {");
        builder.AppendLine("            return;");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        exports.Add(new global::SharpEmu.HLE.ExportedFunction(libraryName, nid, exportName, target, function));");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        context.AddSource("SysAbiExportRegistry.g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
    }

    /// <summary>
    /// SysV integer unmarshalling: the first six parameters read RDI, RSI, RDX, RCX,
    /// R8, and R9; later parameters read consecutive eight-byte stack slots after the
    /// return address. [GuestCString] parameters treat their raw value as a guest
    /// pointer and marshal a bounded null-terminated UTF-8 string before invocation.
    /// Full-width and unsigned returns are written explicitly so the int-based runtime
    /// dispatch contract cannot truncate or sign-extend the guest-visible RAX value.
    /// </summary>
    private static string TypedThunk(ExportModel export)
    {
        var kinds = export.TypedParameterKinds.Length == 0
            ? Array.Empty<string>()
            : export.TypedParameterKinds.Split(',');
        var arguments = new string[kinds.Length];
        var reads = new StringBuilder();
        for (var index = 0; index < kinds.Length; index++)
        {
            string rawValue;
            if (index < SysAbiExportShape.ArgumentRegisters.Length)
            {
                rawValue = "ctx[global::SharpEmu.HLE.CpuRegister." +
                    SysAbiExportShape.ArgumentRegisters[index] + "]";
            }
            else
            {
                var variable = "stackArg" + index;
                var stackOffset = 8UL + ((ulong)(index - SysAbiExportShape.ArgumentRegisters.Length) * 8UL);
                reads.AppendLine(
                    $"            if (!ctx.TryReadUInt64(ctx[global::SharpEmu.HLE.CpuRegister.Rsp] + {stackOffset}UL, out var {variable}))");
                reads.AppendLine("            {");
                reads.AppendLine("                return ctx.SetReturn(global::SharpEmu.HLE.OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);");
                reads.AppendLine("            }");
                reads.AppendLine();
                rawValue = variable;
            }

            if (kinds[index].StartsWith("cstring:", StringComparison.Ordinal))
            {
                var maxLength = kinds[index].Substring("cstring:".Length);
                var variable = "guestString" + index;
                reads.AppendLine($"            if (!ctx.TryReadNullTerminatedUtf8({rawValue}, {maxLength}, out var {variable}))");
                reads.AppendLine("            {");
                reads.AppendLine("                return ctx.SetReturn(global::SharpEmu.HLE.OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);");
                reads.AppendLine("            }");
                reads.AppendLine();
                arguments[index] = variable;
                continue;
            }

            arguments[index] = kinds[index] == "ulong"
                ? rawValue
                : "unchecked((" + kinds[index] + ")" + rawValue + ")";
        }

        var handlerArguments = export.Shape switch
        {
            SysAbiExportShape.HandlerShape.Parameterless => string.Empty,
            SysAbiExportShape.HandlerShape.ContextOnly => "ctx",
            _ => "ctx, " + string.Join(", ", arguments),
        };
        var invocation = export.ContainingType + "." + export.MethodName + "(" + handlerArguments + ")";
        if (reads.Length == 0 && export.ReturnKind == SysAbiExportShape.ReturnKind.Int32)
        {
            return "static ctx => " + invocation;
        }

        var body = ReturnBody(export.ReturnKind, invocation);

        var builder = new StringBuilder();
        builder.AppendLine("static ctx =>");
        builder.AppendLine("        {");
        builder.Append(reads);
        builder.Append(body);
        builder.Append("        }");
        return builder.ToString();
    }

    private static string ReturnBody(SysAbiExportShape.ReturnKind returnKind, string invocation)
    {
        var builder = new StringBuilder();
        if (returnKind == SysAbiExportShape.ReturnKind.Int32)
        {
            builder.AppendLine("            return " + invocation + ";");
            return builder.ToString();
        }

        if (returnKind == SysAbiExportShape.ReturnKind.Void)
        {
            builder.AppendLine("            " + invocation + ";");
            builder.AppendLine("            ctx[global::SharpEmu.HLE.CpuRegister.Rax] = 0;");
            builder.AppendLine("            return 0;");
            return builder.ToString();
        }

        builder.AppendLine("            var result = " + invocation + ";");
        var guestResult = returnKind switch
        {
            SysAbiExportShape.ReturnKind.UInt32 => "(ulong)result",
            SysAbiExportShape.ReturnKind.Int64 => "unchecked((ulong)result)",
            SysAbiExportShape.ReturnKind.UInt64 => "result",
            _ => throw new InvalidOperationException("Unsupported SysAbi return kind."),
        };
        builder.AppendLine(
            "            ctx[global::SharpEmu.HLE.CpuRegister.Rax] = " + guestResult + ";");
        builder.AppendLine("            return unchecked((int)result);");
        return builder.ToString();
    }

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

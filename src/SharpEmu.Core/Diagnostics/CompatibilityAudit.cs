// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.Core.Diagnostics;

public sealed record CompatibilityAuditImage(
    string Path,
    IReadOnlyCollection<string> ImportedNids,
    IReadOnlyCollection<string>? ExportedSymbols = null);

public sealed record CompatibilityAuditFailure(
    string Path,
    string Error);

public sealed record CompatibilityAuditImport(
    string Nid,
    string Name,
    CompatibilityAuditResolution Resolution,
    IReadOnlyList<string> RequiredBy);

public enum CompatibilityAuditResolution
{
    Missing,
    Hle,
    Module,
    Runtime,
}

public sealed record CompatibilityAuditReport(
    IReadOnlyList<CompatibilityAuditImage> Images,
    IReadOnlyList<CompatibilityAuditFailure> Failures,
    IReadOnlyList<CompatibilityAuditImport> Imports)
{
    public int UniqueImportCount => Imports.Count;

    public int HleImportCount => Imports.Count(static item => item.Resolution == CompatibilityAuditResolution.Hle);

    public int ModuleImportCount => Imports.Count(static item => item.Resolution == CompatibilityAuditResolution.Module);

    public int RuntimeImportCount => Imports.Count(static item => item.Resolution == CompatibilityAuditResolution.Runtime);

    public int ResolvedImportCount => HleImportCount + ModuleImportCount + RuntimeImportCount;

    public int MissingImportCount => Imports.Count(static item => item.Resolution == CompatibilityAuditResolution.Missing);
}

/// <summary>
/// Inspects decrypted ELF/fSELF images without executing guest code and compares
/// their imported NIDs with SharpEmu's generated HLE export registry.
/// </summary>
public static class CompatibilityAudit
{
    private static readonly HashSet<string> RuntimeResolvedNids = new(StringComparer.Ordinal)
    {
        "LwG8g3niqwA", // sceKernelDlsym
        "r8mvOaWdi28", // IL2CPP API lookup bridge
    };

    private static readonly string[] SupportedExtensions =
        [".bin", ".elf", ".prx", ".sprx"];

    public static CompatibilityAuditReport AnalyzePath(
        string path,
        IModuleManager moduleManager,
        ISymbolCatalog symbolCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(moduleManager);
        ArgumentNullException.ThrowIfNull(symbolCatalog);

        var fullPath = Path.GetFullPath(path);
        var candidates = DiscoverImages(fullPath);
        var images = new List<CompatibilityAuditImage>(candidates.Count);
        var failures = new List<CompatibilityAuditFailure>();
        foreach (var candidate in candidates)
        {
            try
            {
                images.Add(InspectImage(candidate, moduleManager));
            }
            catch (Exception exception)
            {
                failures.Add(new CompatibilityAuditFailure(candidate, exception.Message));
            }
        }

        return BuildReport(images, failures, moduleManager, symbolCatalog);
    }

    public static CompatibilityAuditReport BuildReport(
        IReadOnlyList<CompatibilityAuditImage> images,
        IReadOnlyList<CompatibilityAuditFailure> failures,
        IModuleManager moduleManager,
        ISymbolCatalog symbolCatalog)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(moduleManager);
        ArgumentNullException.ThrowIfNull(symbolCatalog);

        var requiredByNid = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var moduleSymbols = new HashSet<string>(
            images.SelectMany(static image => image.ExportedSymbols ?? Array.Empty<string>()),
            StringComparer.Ordinal);
        foreach (var image in images)
        {
            var displayPath = Path.GetFileName(image.Path);
            foreach (var nid in image.ImportedNids)
            {
                if (string.IsNullOrWhiteSpace(nid))
                {
                    continue;
                }

                if (!requiredByNid.TryGetValue(nid, out var requiredBy))
                {
                    requiredBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    requiredByNid.Add(nid, requiredBy);
                }

                requiredBy.Add(displayPath);
            }
        }

        var imports = new List<CompatibilityAuditImport>(requiredByNid.Count);
        foreach (var pair in requiredByNid.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var hleImplemented = moduleManager.TryGetExport(pair.Key, out var export);
            var resolution = hleImplemented
                ? CompatibilityAuditResolution.Hle
                : moduleSymbols.Contains(pair.Key)
                    ? CompatibilityAuditResolution.Module
                    : RuntimeResolvedNids.Contains(pair.Key)
                        ? CompatibilityAuditResolution.Runtime
                        : CompatibilityAuditResolution.Missing;
            var name = hleImplemented
                ? export.Name
                : symbolCatalog.TryGetByNid(pair.Key, out var symbol)
                    ? symbol.ExportName
                    : "<unknown>";
            imports.Add(new CompatibilityAuditImport(
                pair.Key,
                name,
                resolution,
                pair.Value.ToArray()));
        }

        return new CompatibilityAuditReport(images, failures, imports);
    }

    private static IReadOnlyList<string> DiscoverImages(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("The compatibility-audit path does not exist.", path);
        }

        return Directory
            .EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(IsCandidateImage)
            .OrderBy(static candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsCandidateImage(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("._", StringComparison.Ordinal))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // A game directory can contain thousands of arbitrary asset *.bin
        // files. Only the conventional executable name is auto-discovered;
        // callers can still audit any specifically supplied file.
        return !extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase);
    }

    private static CompatibilityAuditImage InspectImage(
        string path,
        IModuleManager moduleManager)
    {
        var bytes = File.ReadAllBytes(path);
        var loader = new SelfLoader();
        var memory = new VirtualMemory();

        // SelfLoader's normal diagnostics are useful during execution but would
        // drown out the audit report. Inspection is single-threaded and runs
        // before emulation starts, so temporarily silencing the process writers
        // is safe here.
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            var image = loader.Load(bytes, memory, moduleManager);
            var importedNids = image.ImportedRelocations
                .Select(static relocation => relocation.Nid)
                .Concat(image.ImportStubs.Values)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static nid => nid, StringComparer.Ordinal)
                .ToArray();
            return new CompatibilityAuditImage(
                path,
                importedNids,
                image.RuntimeSymbols.Keys.ToArray());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

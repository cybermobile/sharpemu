// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Diagnostics;
using SharpEmu.HLE;
using System.Text.Json;

namespace SharpEmu.CLI;

internal static partial class Program
{
    private static bool TryRunCompatibilityAudit(string[] args, out int exitCode)
    {
        exitCode = 0;
        var auditIndex = Array.FindIndex(
            args,
            static argument => string.Equals(argument, "--audit-compat", StringComparison.OrdinalIgnoreCase));
        if (auditIndex < 0)
        {
            return false;
        }

        EnsureCliConsole();
        UseUtf8ConsoleOutput();
        if (auditIndex + 1 >= args.Length || args[auditIndex + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("[AUDIT][ERROR] --audit-compat requires an image or directory path.");
            exitCode = 1;
            return true;
        }

        var auditPath = args[auditIndex + 1];
        string? jsonPath = null;
        foreach (var argument in args)
        {
            const string prefix = "--audit-json=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                jsonPath = argument[prefix.Length..];
            }
        }

        try
        {
            var moduleManager = new ModuleManager();
            moduleManager.RegisterExports(
                SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5));
            var report = CompatibilityAudit.AnalyzePath(auditPath, moduleManager, Aerolib.Instance);

            Console.WriteLine(
                $"[AUDIT] images={report.Images.Count} failures={report.Failures.Count} " +
                $"imports={report.UniqueImportCount} resolved={report.ResolvedImportCount} " +
                $"hle={report.HleImportCount} modules={report.ModuleImportCount} runtime={report.RuntimeImportCount} " +
                $"missing={report.MissingImportCount}");
            const int maxConsoleMissing = 200;
            var missingImports = report.Imports
                .Where(static item => item.Resolution == CompatibilityAuditResolution.Missing)
                .ToArray();
            foreach (var missing in missingImports.Take(maxConsoleMissing))
            {
                Console.WriteLine(
                    $"[AUDIT][MISSING] {missing.Nid} {missing.Name} " +
                    $"required_by={string.Join(',', missing.RequiredBy)}");
            }
            if (missingImports.Length > maxConsoleMissing)
            {
                Console.WriteLine(
                    $"[AUDIT] ... {missingImports.Length - maxConsoleMissing} additional missing imports; " +
                    "use --audit-json=<path> for the complete inventory.");
            }

            foreach (var failure in report.Failures)
            {
                Console.WriteLine(
                    $"[AUDIT][UNREADABLE] {failure.Path}: {failure.Error}");
            }

            if (!string.IsNullOrWhiteSpace(jsonPath))
            {
                var fullJsonPath = Path.GetFullPath(jsonPath);
                var parent = Path.GetDirectoryName(fullJsonPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllText(
                    fullJsonPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"[AUDIT] JSON report: {fullJsonPath}");
            }

            exitCode = report.MissingImportCount == 0 && report.Failures.Count == 0 ? 0 : 7;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[AUDIT][ERROR] {exception.Message}");
            exitCode = 1;
        }

        return true;
    }
}

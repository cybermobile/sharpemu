// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(App0EnvironmentCollection.Name)]
public sealed class KernelGuestPathTests
{
    [Fact]
    public void UnrealRelativeAssetPathsResolveInsideApplicationRoot()
    {
        var appRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-kernel-path-{Guid.NewGuid():N}");
        var pakPath = CreateFile(
            appRoot,
            "trf",
            "content",
            "paks",
            "pakchunk0-ps5.pak");
        var bankPath = CreateFile(
            appRoot,
            "trf",
            "content",
            "fmod",
            "ps5",
            "master.bank");
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", appRoot);

            Assert.Equal(
                pakPath,
                KernelMemoryCompatExports.ResolveGuestPath(
                    "../../../TRF/Content/Paks/pakchunk0-PS5.pak"),
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                bankPath,
                KernelMemoryCompatExports.ResolveGuestPath(
                    "../../../TRF/Content/FMOD/PS5/Master.bank"),
                StringComparer.OrdinalIgnoreCase);

            var escaped = KernelMemoryCompatExports.ResolveGuestPath(
                "trf/../../outside.dat");
            Assert.Equal(
                Path.Combine(appRoot, "outside.dat"),
                escaped,
                StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(
                Path.TrimEndingDirectorySeparator(appRoot) + Path.DirectorySeparatorChar,
                Path.GetFullPath(escaped),
                StringComparison.OrdinalIgnoreCase);

            Assert.Equal(
                escaped,
                KernelMemoryCompatExports.ResolveGuestPath(
                    "/app0/../../outside.dat"),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public void App0RootRefreshesBetweenGameSessions()
    {
        var firstRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-kernel-path-first-{Guid.NewGuid():N}");
        var secondRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-kernel-path-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", firstRoot);
            Assert.Equal(
                Path.Combine(firstRoot, "asset.bin"),
                KernelMemoryCompatExports.ResolveGuestPath("/app0/asset.bin"));

            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", secondRoot);
            Assert.Equal(
                Path.Combine(secondRoot, "asset.bin"),
                KernelMemoryCompatExports.ResolveGuestPath("/app0/asset.bin"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    private static string CreateFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }
}

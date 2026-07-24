// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(App0EnvironmentCollection.Name)]
public sealed class AvPlayerPathTests
{
    [Fact]
    public void UnrealRelativePathResolvesCaseInsensitivePackagedMovie()
    {
        var appRoot = CreateAppRoot(out var moviePath);
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", appRoot);

            var resolved = AvPlayerExports.ResolveGuestPath(
                "../../../trf/Content/Movies/Fake_Intro.mp4");

            Assert.Equal(moviePath, resolved, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public void InternalParentTraversalDoesNotEscapeApplicationRoot()
    {
        var appRoot = CreateAppRoot(out _);
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", appRoot);

            Assert.Null(AvPlayerExports.ResolveGuestPath("trf/../../outside.mp4"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(appRoot, recursive: true);
        }
    }

    private static string CreateAppRoot(out string moviePath)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-avplayer-{Guid.NewGuid():N}");
        var movieDirectory = Path.Combine(root, "trf", "content", "movies");
        Directory.CreateDirectory(movieDirectory);
        moviePath = Path.Combine(movieDirectory, "fake_intro.mp4");
        File.WriteAllBytes(moviePath, [0]);
        return root;
    }
}

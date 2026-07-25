// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Diagnostics;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

public sealed class CompatibilityAuditTests
{
    [Fact]
    public void BuildReport_ClassifiesAndDeduplicatesImportsAcrossImages()
    {
        var moduleManager = new ModuleManager();
        moduleManager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var images = new[]
        {
            new CompatibilityAuditImage(
                "/game/eboot.bin",
                ["crb5j7mkk1c", "LwG8g3niqwA", "missing-nid"]),
            new CompatibilityAuditImage(
                "/game/sce_module/game.prx",
                ["missing-nid", "module-nid"],
                ["module-nid"]),
        };

        var report = CompatibilityAudit.BuildReport(
            images,
            [],
            moduleManager,
            Aerolib.Instance);

        Assert.Equal(4, report.UniqueImportCount);
        Assert.Equal(1, report.HleImportCount);
        Assert.Equal(1, report.ModuleImportCount);
        Assert.Equal(1, report.RuntimeImportCount);
        Assert.Equal(1, report.MissingImportCount);

        var signalReturn = Assert.Single(report.Imports, item => item.Nid == "crb5j7mkk1c");
        Assert.Equal(CompatibilityAuditResolution.Hle, signalReturn.Resolution);
        Assert.Equal("_is_signal_return", signalReturn.Name);

        var module = Assert.Single(report.Imports, item => item.Nid == "module-nid");
        Assert.Equal(CompatibilityAuditResolution.Module, module.Resolution);

        var runtime = Assert.Single(report.Imports, item => item.Nid == "LwG8g3niqwA");
        Assert.Equal(CompatibilityAuditResolution.Runtime, runtime.Resolution);

        var missing = Assert.Single(report.Imports, item => item.Nid == "missing-nid");
        Assert.Equal(CompatibilityAuditResolution.Missing, missing.Resolution);
        Assert.Equal(["eboot.bin", "game.prx"], missing.RequiredBy);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Content invariants over the compile-time generated export registry
/// (SharpEmu.Generated.SysAbiExportRegistry), which is the runtime's sole registration
/// source. Replaces the parity test that pinned the registry to the retired reflection
/// scan while both existed; equality with the scan proved the swap, these pin what must
/// stay true now that only the registry remains.
/// </summary>
public sealed class SysAbiRegistryTests
{
    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    [InlineData(Generation.Gen4 | Generation.Gen5)]
    public void RegistryIsDuplicateFree(Generation generation)
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation);
        var manager = new ModuleManager();

        // RegisterExports skips NIDs it has already seen, so a shortfall here means the
        // generated table carries a duplicate the SHEM001 analyzer should have caught.
        Assert.Equal(exports.Count, manager.RegisterExports(exports));
    }

    [Fact]
    public void RegistryCoversTheFullExportSurface()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5);

        // 715 exports existed when the registry replaced the scan; shrinkage means the
        // generator silently dropped handlers.
        Assert.True(exports.Count >= 715, $"registry shrank to {exports.Count} exports");
    }

    [Fact]
    public void RegistryResolvesKnownExportWithCatalogIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport("Zxa0VhQVTsk", out var export));
        Assert.Equal("sceKernelWaitSema", export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    [Theory]
    [InlineData("DiGVep5yB5w", "_ZSt13_Execute_onceRSt9once_flagPFiPvS1_PS1_ES1_")]
    [InlineData("MQFPAqQPt1s", "__cxa_decrement_exception_refcount")]
    [InlineData("PsrRUg671K0", "__cxa_increment_exception_refcount")]
    [InlineData("bRujIheWlB0", "_ZSt14_Throw_C_errori")]
    public void RegistryResolvesCxxRuntimeCompatibilityExports(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
    }

    [Theory]
    [InlineData("KuOuD58hqn4", "malloc_stats_fast")]
    [InlineData("YaHc3GS7y7g", "_Mtx_init")]
    [InlineData("SreZybSRWpU", "_Cnd_init")]
    [InlineData("-P6FNMzk2Kc", "cosf")]
    [InlineData("YQ0navp+YIc", "puts")]
    [InlineData("1uJgoVq3bQU", "_Getptolower")]
    [InlineData("35NoyMOtYpE", "SetDataFolder")]
    [InlineData("cJ2Y4E-t258", "il2cpp_api_register_symbols")]
    [InlineData("-pnj3-7a6QA", "unity_mono_set_user_malloc_mutex")]
    [InlineData("M4YYbSFfJ8g", "setenv")]
    [InlineData("1Pk0qZQGeWo", "sscanf")]
    [InlineData("AcslpN1jHR8", "scePadDeviceClassGetExtendedInformation")]
    public void RegistryResolvesRuntimeCompatibilityExports(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
    }
}

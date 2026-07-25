// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelGetTscFrequency must describe the same clock that sceKernelReadTsc returns. ReadTsc
// only returns the CPU's RDTSC when the host RDTSC reader is available (64-bit Windows) and
// otherwise falls back to the QPC-based Stopwatch, so the frequency selection has to follow suit.
public sealed class KernelRuntimeCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;

    [Fact]
    public void RegistryIncludesSysmoduleGetModuleInfoForUnwind()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("4fU5yvOkVG4", out var export));
        Assert.Equal("sceSysmoduleGetModuleInfoForUnwind", export.Name);
        Assert.Equal("libSceSysmodule", export.LibraryName);
    }

    [Fact]
    public void SysmoduleGetModuleInfoForUnwind_UsesKernelValidation()
    {
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x8_0000_0000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;

        var result = KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void RegistryIncludesIsSignalReturn()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("crb5j7mkk1c", out var export));
        Assert.Equal("_is_signal_return", export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    [Fact]
    public void IsSignalReturn_ReportsNoGuestSignalTrampoline()
    {
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1_B909_10C5;
        context[CpuRegister.Rax] = ulong.MaxValue;

        var result = KernelRuntimeCompatExports.IsSignalReturn(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static KernelRuntimeCompatExports.TryGetFrequency Yields(ulong hz) =>
        (out ulong frequencyHz) =>
        {
            frequencyHz = hz;
            return true;
        };

    private static readonly KernelRuntimeCompatExports.TryGetFrequency Fails =
        (out ulong frequencyHz) =>
        {
            frequencyHz = 0;
            return false;
        };

    [Fact]
    public void WithoutHostRdtsc_ReportsStopwatchFrequency_NotHardwareTsc()
    {
        // Regression: on Linux/macOS ReadTsc returns the Stopwatch counter, so the reported
        // frequency must be the Stopwatch's, never the CPU's much larger hardware TSC frequency.
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void WithHostRdtsc_PrefersCalibratedFrequency()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(2_400_000_000UL, frequencyHz);
        Assert.Equal("calibrated-rdtsc", source);
    }

    [Fact]
    public void WithHostRdtsc_FallsBackToCpuid_WhenCalibrationFails()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(3_000_000_000UL, frequencyHz);
        Assert.Equal("cpuid", source);
    }

    [Fact]
    public void WithHostRdtsc_UsesStopwatch_WhenRdtscFrequencyUnknown()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnvOverride_Wins_WhenSane(bool rdtscAvailable)
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable,
            overrideHzText: "1500000000",
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(1_500_000_000UL, frequencyHz);
        Assert.Equal("env", source);
    }

    [Fact]
    public void EnvOverride_BelowMinimum_IsIgnored()
    {
        // 500 kHz is below the sanity floor, so it is dropped; with rdtsc unavailable the
        // hardware-TSC path is gated off and the Stopwatch frequency is used.
        var (frequencyHz, _) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: "500000",
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
    }

    [Fact]
    public void NonPositiveStopwatchFrequency_FallsBackToDefault()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 0);

        Assert.Equal(10_000_000UL, frequencyHz); // DefaultKernelTscFrequency
        Assert.Equal("qpc", source);
    }
}

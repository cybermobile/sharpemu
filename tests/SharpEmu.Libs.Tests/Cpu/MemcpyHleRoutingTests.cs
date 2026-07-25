// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class MemcpyHleRoutingTests
{
    private const string MemcpyNid = "Q3VBxCXhUHs";
    private const string MemsetNid = "QrZZdJ8XsX0";
    private const string Il2CppApiLookupNid = "r8mvOaWdi28";
    private const string RdtscNid = "-2IRUCO--PM";

    [Fact]
    public void IsHlePreferredNid_PrefersHleForMemcpy_OnEveryPlatform()
    {
        Assert.True(
            InvokeIsHlePreferredNid(MemcpyNid),
            $"memcpy ({MemcpyNid}) must route through HLE on every platform. It was previously " +
            "gated behind OperatingSystem.IsWindows(), which left Linux and macOS on the LLE " +
            "intrinsic stub and faulted in guest code. Do not reintroduce an OS condition here.");
    }

    [Fact]
    public void IsHlePreferredNid_PrefersHleForMemset()
    {
        Assert.True(
            InvokeIsHlePreferredNid(MemsetNid),
            $"memset ({MemsetNid}) must route through HLE on every platform.");
    }

    [Fact]
    public void IsHlePreferredNid_PrefersIl2CppApiLookupOverLoadedModuleSymbol()
    {
        Assert.True(
            InvokeIsHlePreferredNid(Il2CppApiLookupNid),
            $"IL2CPP API lookup ({Il2CppApiLookupNid}) must reach its HLE dispatcher. " +
            "Routing it directly to a same-NID guest symbol bypasses symbol-name resolution " +
            "and leaves Unity's IL2CPP callback table null.");
    }

    [Fact]
    public void CompleteIl2CppApiLookup_ReturnsResolvedAddressInRax()
    {
        const ulong resolvedAddress = 0x0000_0008_1234_5678;
        var context = new CpuContext(
            new FakeCpuMemory(0x0000_7FFF_3000_0000, 0x1000),
            Generation.Gen5);
        context[CpuRegister.Rsi] = 0x0000_7FFF_3000_0080;

        DirectExecutionBackend.CompleteIl2CppApiLookup(
            context,
            resolved: true,
            resolvedAddress);

        Assert.Equal(resolvedAddress, context[CpuRegister.Rax]);
        Assert.Equal(0x0000_7FFF_3000_0080UL, context[CpuRegister.Rsi]);
    }

    [Fact]
    public void CompleteIl2CppApiLookup_ReturnsNullWhenSymbolIsMissing()
    {
        var context = new CpuContext(
            new FakeCpuMemory(0x0000_7FFF_3000_0000, 0x1000),
            Generation.Gen5);
        context[CpuRegister.Rax] = ulong.MaxValue;

        DirectExecutionBackend.CompleteIl2CppApiLookup(
            context,
            resolved: false,
            resolvedAddress: 0x0000_0008_1234_5678);

        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void TryResolveIl2CppApiAddress_UsesComputedNidWhenCatalogNameIsMissing()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        const string symbolName = "il2cpp_debugger_set_agent_options";
        const string computedNid = "Sgpj9H-vi4o";
        const ulong guestAddress = 0x0000_0008_1234_5678;
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));
        var symbolsField = typeof(DirectExecutionBackend).GetField(
            "_runtimeSymbolsByName",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(symbolsField);
        symbolsField.SetValue(
            backend,
            new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                [computedNid] = guestAddress,
            });

        var method = typeof(DirectExecutionBackend).GetMethod(
            "TryResolveIl2CppApiAddress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = [symbolName, null];

        Assert.True((bool)method.Invoke(backend, args)!);
        Assert.Equal(guestAddress, (ulong)args[1]!);
    }

    [Fact]
    public void TryCreateNativeImportIntrinsic_DoesNotClaimMemcpy()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var claimed = InvokeTryCreateNativeImportIntrinsic(MemcpyNid, out var address);

        Assert.False(
            claimed,
            $"memcpy ({MemcpyNid}) must fall through to the HLE trampoline. SetupImportStubs tries " +
            "the intrinsic stub before the trampoline, so without an IsHlePreferredNid guard here " +
            "the intrinsic claims memcpy and the HLE routing never takes effect.");
        Assert.Equal(0, address);
    }

    [Fact]
    public void TryCreateNativeImportIntrinsic_StillClaimsNonHleNids()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var claimed = InvokeTryCreateNativeImportIntrinsic(RdtscNid, out var address);

        Assert.True(
            claimed,
            $"rdtsc ({RdtscNid}) has no HLE handler and must still receive an intrinsic stub. If " +
            "this fails the memcpy assertions above may be passing vacuously.");
        Assert.NotEqual(0, address);

        unsafe
        {
            Assert.True(HostMemory.Free((void*)address, 0, HostMemory.MEM_RELEASE));
        }
    }

    [Fact]
    public void IsHlePreferredNid_DoesNotBranchOnHostOperatingSystem()
    {
        var method = typeof(DirectExecutionBackend).GetMethod(
            "IsHlePreferredNid",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var callees = ResolveCallees(method);

        Assert.DoesNotContain(callees, static name =>
            name is "IsWindows" or "IsLinux" or "IsMacOS" or "IsOSPlatform" or "IsFreeBSD");
    }

    private static HashSet<string> ResolveCallees(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);

        var module = method.Module;
        var generic = method.DeclaringType?.GetGenericArguments();
        var callees = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var callee = module.ResolveMethod(token, generic, null);
                if (callee?.Name is { } name)
                {
                    callees.Add(name);
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return callees;
    }

    private static bool InvokeIsHlePreferredNid(string nid)
    {
        var method = typeof(DirectExecutionBackend).GetMethod(
            "IsHlePreferredNid",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [nid])!;
    }

    private static bool InvokeTryCreateNativeImportIntrinsic(string nid, out nint address)
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));
        var trampolineList = typeof(DirectExecutionBackend).GetField(
            "_importHandlerTrampolines",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trampolineList);
        trampolineList.SetValue(backend, new List<nint>());

        var method = typeof(DirectExecutionBackend).GetMethod(
            "TryCreateNativeImportIntrinsic",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = [nid, null];
        var claimed = (bool)method.Invoke(backend, args)!;
        address = (nint)args[1]!;
        return claimed;
    }
}

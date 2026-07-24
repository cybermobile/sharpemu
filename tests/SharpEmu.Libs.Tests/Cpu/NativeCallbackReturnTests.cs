// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeCallbackReturnTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong ReturnU64Delegate();

    [Fact]
    public void NativeCallbackBoundary_PreservesFullWidthPointerReturn()
    {
        const ulong expected = 0x1234_5678_ABCD_EF01UL;
        ReturnU64Delegate callback = static () => expected;
        var callbackAddress = Marshal.GetFunctionPointerForDelegate(callback);
        var callNativeEntry = typeof(DirectExecutionBackend).GetMethod(
            "CallNativeEntryU64",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(callNativeEntry);
        Assert.Equal(typeof(ulong), callNativeEntry.ReturnType);
        var actual = (ulong)callNativeEntry.Invoke(null, [callbackAddress])!;
        GC.KeepAlive(callback);

        Assert.Equal(expected, actual);
    }
}

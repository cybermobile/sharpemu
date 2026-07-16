// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CxxAbi;

public static class CxxRuntimeExports
{
    private const int ExecuteOnceUninitialized = 0;
    private const int ExecuteOnceComplete = 1;
    private const int ExecuteOnceInProgress = 2;

    private sealed class OnceGate
    {
        public int OwnerThreadId { get; set; }
    }

    private sealed class OnceGateTable
    {
        public ConcurrentDictionary<ulong, OnceGate> Gates { get; } = new();
    }

    private static readonly ConditionalWeakTable<ICpuMemory, OnceGateTable> _onceGateTables = new();

    [SysAbiExport(
        Nid = "DiGVep5yB5w",
        ExportName = "_ZSt13_Execute_onceRSt9once_flagPFiPvS1_PS1_ES1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int ExecuteOnce(CpuContext ctx)
    {
        var flagAddress = ctx[CpuRegister.Rdi];
        var callback = ctx[CpuRegister.Rsi];
        var opaqueArgument = ctx[CpuRegister.Rdx];
        if (flagAddress == 0 || callback == 0 || !TryReadState(ctx, flagAddress, out _))
        {
            return ReturnGuestBool(ctx, false);
        }

        var gate = GetOnceGate(ctx.Memory, flagAddress);
        var currentThreadId = Environment.CurrentManagedThreadId;
        lock (gate)
        {
            while (true)
            {
                if (!TryReadState(ctx, flagAddress, out var state))
                {
                    return ReturnGuestBool(ctx, false);
                }

                if (state == ExecuteOnceComplete)
                {
                    return ReturnGuestBool(ctx, true);
                }

                if (state == ExecuteOnceInProgress)
                {
                    if (gate.OwnerThreadId == currentThreadId)
                    {
                        return ReturnGuestBool(ctx, false);
                    }

                    // An in-progress value without a host owner can only be stale: every
                    // initializer claimed by this export records the owner before unlocking.
                    if (gate.OwnerThreadId == 0)
                    {
                        if (!TryWriteState(ctx, flagAddress, ExecuteOnceUninitialized))
                        {
                            return ReturnGuestBool(ctx, false);
                        }

                        continue;
                    }

                    Monitor.Wait(gate);
                    continue;
                }

                if (state != ExecuteOnceUninitialized ||
                    !TryWriteState(ctx, flagAddress, ExecuteOnceInProgress))
                {
                    return ReturnGuestBool(ctx, false);
                }

                gate.OwnerThreadId = currentThreadId;
                break;
            }
        }

        var callbackSucceeded = false;
        ulong scratchAddress = 0;
        IGuestMemoryAllocator? allocator = null;
        try
        {
            allocator = ctx.Memory as IGuestMemoryAllocator;
            var scheduler = GuestThreadExecution.Scheduler;
            if (allocator is not null &&
                scheduler is not null &&
                allocator.TryAllocateGuestMemory(sizeof(ulong), sizeof(ulong), out scratchAddress) &&
                ctx.TryWriteUInt64(scratchAddress, 0) &&
                scheduler.TryCallGuestFunction(
                    ctx,
                    callback,
                    flagAddress,
                    opaqueArgument,
                    scratchAddress,
                    stackAddress: 0,
                    stackSize: 0,
                    "std::_Execute_once",
                    out var callbackResult,
                    out _))
            {
                callbackSucceeded = callbackResult != 0;
            }
        }
        finally
        {
            if (scratchAddress != 0)
            {
                _ = allocator?.TryFreeGuestMemory(scratchAddress);
            }

            lock (gate)
            {
                if (!TryWriteState(
                    ctx,
                    flagAddress,
                    callbackSucceeded ? ExecuteOnceComplete : ExecuteOnceUninitialized))
                {
                    callbackSucceeded = false;
                }

                gate.OwnerThreadId = 0;
                Monitor.PulseAll(gate);
            }
        }

        return ReturnGuestBool(ctx, callbackSucceeded);
    }

    [SysAbiExport(
        Nid = "MQFPAqQPt1s",
        ExportName = "__cxa_decrement_exception_refcount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaDecrementExceptionRefcount(CpuContext ctx) => ReturnGuestVoid(ctx);

    [SysAbiExport(
        Nid = "PsrRUg671K0",
        ExportName = "__cxa_increment_exception_refcount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaIncrementExceptionRefcount(CpuContext ctx) => ReturnGuestVoid(ctx);

    [SysAbiExport(
        Nid = "bRujIheWlB0",
        ExportName = "_ZSt14_Throw_C_errori",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int ThrowCError(CpuContext ctx)
    {
        // This is a noreturn helper in the Sony C++ runtime. Compatibility initializers
        // now return their real status, so valid startup never reaches it. A host-side
        // exception cannot model guest unwinding and would terminate the emulator.
        return ReturnGuestVoid(ctx);
    }

    private static OnceGate GetOnceGate(ICpuMemory memory, ulong flagAddress)
    {
        while (memory is ICpuMemoryWrapper wrapper && !ReferenceEquals(wrapper.Inner, memory))
        {
            memory = wrapper.Inner;
        }

        return _onceGateTables.GetValue(memory, static _ => new OnceGateTable()).Gates
            .GetOrAdd(flagAddress, static _ => new OnceGate());
    }

    private static bool TryReadState(CpuContext ctx, ulong address, out int state)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            state = 0;
            return false;
        }

        state = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryWriteState(CpuContext ctx, ulong address, int state)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, state);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int ReturnGuestBool(CpuContext ctx, bool value)
    {
        ctx[CpuRegister.Rax] = value ? 1UL : 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnGuestVoid(CpuContext ctx)
    {
        // Null is an exact libc++abi no-op. Non-null exception ownership needs the
        // platform's hidden exception layout, destructor callback, and matching free
        // implementation; until that surface exists, a no-op is safer than corrupting
        // an unknown header or inventing a disconnected host-side reference count.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}

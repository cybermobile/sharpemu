// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventFlagCompatExports
{
    private const int MaxEventFlagNameLength = 31;
    private const int HostWaitPumpMilliseconds = 1;
    private const uint AttrThreadFifo = 0x01;
    private const uint AttrThreadPriority = 0x02;
    private const uint AttrSingle = 0x10;
    private const uint AttrMulti = 0x20;
    private const uint WaitAnd = 0x01;
    private const uint WaitOr = 0x02;
    private const uint ClearAll = 0x10;
    private const uint ClearPattern = 0x20;

    private static readonly ConcurrentDictionary<ulong, EventFlagState> _eventFlags = new();
    private static long _nextEventFlagHandle = 1;

    // Cached once: gating every call site avoids building the interpolated
    // trace string (and FormatFrameChain/FormatGuestWaitObject) when disabled.
    private static readonly bool _traceEventFlag = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_EVENT_FLAG"), "1", StringComparison.Ordinal);

    private sealed class EventFlagState
    {
        public required string Name { get; init; }
        public required uint Attributes { get; init; }
        public ulong Bits { get; set; }
        public int WaitingThreads { get; set; }
        public long CancellationGeneration { get; set; }
        public bool Deleted { get; set; }
        public object Gate { get; } = new();
    }

    [SysAbiExport(
        Nid = "BpFoboUJoZU",
        ExportName = "sceKernelCreateEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateEventFlag(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attributes = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialPattern = ctx[CpuRegister.Rcx];
        var optionAddress = ctx[CpuRegister.R8];

        if (outAddress == 0 ||
            nameAddress == 0 ||
            optionAddress != 0 ||
            !IsValidAttributes(attributes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxEventFlagNameLength + 1, out var name))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        }

        if (Encoding.UTF8.GetByteCount(name) > MaxEventFlagNameLength)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextEventFlagHandle));
        _eventFlags[handle] = new EventFlagState
        {
            Name = name,
            Attributes = attributes,
            Bits = initialPattern,
        };

        if (!ctx.TryWriteUInt64(outAddress, handle))
        {
            _eventFlags.TryRemove(handle, out _);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        }

        if (_traceEventFlag) TraceEventFlag($"create handle=0x{handle:X16} name='{name}' attr=0x{attributes:X2} bits=0x{initialPattern:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "8mql9OcQnd4",
        ExportName = "sceKernelDeleteEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!_eventFlags.TryRemove(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        lock (state.Gate)
        {
            state.Deleted = true;
            Monitor.PulseAll(state.Gate);
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetEventFlagWakeKey(handle));

        if (_traceEventFlag) TraceEventFlag($"delete handle=0x{handle:X16} name='{state.Name}'");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "IOnSvHzqu6A",
        ExportName = "sceKernelSetEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var returnRip = GetCurrentReturnRip();
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        lock (state.Gate)
        {
            state.Bits |= pattern;
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag) TraceEventFlag($"set handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetEventFlagWakeKey(handle));
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "7uhBFWRAS60",
        ExportName = "sceKernelClearEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClearEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        lock (state.Gate)
        {
            state.Bits &= pattern;
            if (_traceEventFlag) TraceEventFlag($"clear handle=0x{handle:X16} mask=0x{pattern:X16} bits=0x{state.Bits:X16}");
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "9lvj5DjHZiA",
        ExportName = "sceKernelPollEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var waitMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var resultAddress = ctx[CpuRegister.Rcx];

        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        if (pattern == 0 || !IsValidWaitMode(waitMode))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (state.Gate)
        {
            if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
            }

            if (!IsSatisfied(state.Bits, pattern, waitMode))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            ApplyClearMode(state, pattern, waitMode);
            if (_traceEventFlag) TraceEventFlag($"poll handle=0x{handle:X16} pattern=0x{pattern:X16} mode=0x{waitMode:X2} bits=0x{state.Bits:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "JTvBflhYazQ",
        ExportName = "sceKernelWaitEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var waitMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var resultAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];
        var returnRip = GetCurrentReturnRip();

        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        if (pattern == 0 || !IsValidWaitMode(waitMode))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        }

        var waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(state.Gate);
        try
        {
            if ((state.Attributes & 0xF0) == AttrSingle && state.WaitingThreads > 0)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED);
            }

            if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var immediateWaitResult))
            {
                WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                return SetReturn(ctx, immediateWaitResult);
            }

            var cancellationGeneration = state.CancellationGeneration;
            var waiterReleased = false;
            void ReleaseWaiterLocked()
            {
                if (waiterReleased)
                {
                    return;
                }

                waiterReleased = true;
                state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
            }

            OrbisGen2Result? GetTerminalResultLocked()
            {
                if (state.Deleted)
                {
                    return OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
                }

                return state.CancellationGeneration != cancellationGeneration
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED
                    : null;
            }

            OrbisGen2Result CompleteTerminalLocked(OrbisGen2Result terminalResult)
            {
                ReleaseWaiterLocked();
                return TryWriteResultPattern(ctx, resultAddress, state.Bits)
                    ? terminalResult
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT;
            }

            state.WaitingThreads++;
            var deadline = timeoutAddress != 0
                ? GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec))
                : 0;

            var currentGuestThread = GuestThreadExecution.CurrentGuestThreadHandle;
            var currentFiber = FiberExports.GetCurrentFiberAddressForDiagnostics(ctx);
            var managedThread = Environment.CurrentManagedThreadId;
            var blockedWaitResult = OrbisGen2Result.ORBIS_GEN2_OK;
            var completed = false;
            var requestedBlock = GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelWaitEventFlag",
                GetEventFlagWakeKey(handle),
                () =>
                {
                    lock (state.Gate)
                    {
                        if (!completed)
                        {
                            ReleaseWaiterLocked();
                            _ = TryWriteResultPattern(ctx, resultAddress, state.Bits);
                            blockedWaitResult = OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
                            completed = true;
                        }
                    }

                    WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                    return (int)blockedWaitResult;
                },
                () =>
                {
                    lock (state.Gate)
                    {
                        var terminalResult = GetTerminalResultLocked();
                        if (terminalResult.HasValue)
                        {
                            blockedWaitResult = CompleteTerminalLocked(terminalResult.Value);
                            completed = true;
                            return true;
                        }

                        if (!TryCompleteSatisfiedWait(
                                ctx,
                                state,
                                pattern,
                                waitMode,
                                resultAddress,
                                out var preparedResult))
                        {
                            return false;
                        }

                        ReleaseWaiterLocked();
                        blockedWaitResult = preparedResult;
                        completed = true;
                        return true;
                    }
                },
                deadline);
            if (_traceEventFlag) TraceEventFlag($"wait-unsatisfied handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} guest_thread=0x{currentGuestThread:X16} fiber=0x{currentFiber:X16} managed={managedThread} block={requestedBlock} ret=0x{returnRip:X16} frames={FormatFrameChain(ctx)}");
            if (_traceEventFlag) TraceEventFlag($"wait-object handle=0x{handle:X16} name='{state.Name}' {FormatGuestWaitObject(ctx)}");
            if (requestedBlock)
            {
                if (_traceEventFlag) TraceEventFlag($"wait-block handle=0x{handle:X16} pattern=0x{pattern:X16} waiters={state.WaitingThreads} guest_thread=0x{currentGuestThread:X16} fiber=0x{currentFiber:X16} managed={managedThread} ret=0x{returnRip:X16}");
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
            }

            if (_traceEventFlag) TraceEventFlag($"wait-host-block handle=0x{handle:X16} pattern=0x{pattern:X16} waiters={state.WaitingThreads}");
            while (true)
            {
                var terminalResult = GetTerminalResultLocked();
                if (terminalResult.HasValue)
                {
                    var result = CompleteTerminalLocked(terminalResult.Value);
                    WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                    return SetReturn(ctx, result);
                }

                if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var waitResult))
                {
                    ReleaseWaiterLocked();
                    WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                    return SetReturn(ctx, waitResult);
                }

                var remainingUsec = ComputeRemainingMicroseconds(timeoutAddress, timeoutUsec, waitStarted);
                if (timeoutAddress != 0 && remainingUsec == 0)
                {
                    ReleaseWaiterLocked();
                    _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                    _ = TryWriteResultPattern(ctx, resultAddress, state.Bits);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }

                var scheduler = GuestThreadExecution.Scheduler;
                var waitMilliseconds = scheduler is not null
                    ? HostWaitPumpMilliseconds
                    : timeoutAddress == 0
                    ? Timeout.Infinite
                    : Math.Max(1, (int)Math.Min((remainingUsec + 999UL) / 1000UL, HostWaitPumpMilliseconds));
                Monitor.Wait(state.Gate, waitMilliseconds);

                if (scheduler is not null)
                {
                    Monitor.Exit(state.Gate);
                    try
                    {
                        scheduler.Pump(ctx, "sceKernelWaitEventFlag");
                    }
                    finally
                    {
                        Monitor.Enter(state.Gate);
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(state.Gate);
        }
    }

    [SysAbiExport(
        Nid = "PZku4ZrXJqg",
        ExportName = "sceKernelCancelEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var setPattern = ctx[CpuRegister.Rsi];
        var waiterCountAddress = ctx[CpuRegister.Rdx];
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        lock (state.Gate)
        {
            if (waiterCountAddress != 0 &&
                !TryWriteUInt32(ctx, waiterCountAddress, unchecked((uint)state.WaitingThreads)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
            }

            state.Bits = setPattern;
            state.CancellationGeneration++;
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag) TraceEventFlag(
                $"cancel handle=0x{handle:X16} bits=0x{setPattern:X16} " +
                $"guest_thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ret=0x{GetCurrentReturnRip():X16}");
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetEventFlagWakeKey(handle));

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool IsValidAttributes(uint attributes)
    {
        var queueMode = attributes & 0x0F;
        var threadMode = attributes & 0xF0;
        return (queueMode is 0 or AttrThreadFifo or AttrThreadPriority) &&
            (threadMode is 0 or AttrSingle or AttrMulti) &&
            (attributes & ~0x33u) == 0;
    }

    private static bool IsValidWaitMode(uint waitMode)
    {
        var condition = waitMode & 0x0F;
        var clearMode = waitMode & 0xF0;
        return condition is WaitAnd or WaitOr &&
            clearMode is 0 or ClearAll or ClearPattern &&
            (waitMode & ~0x33u) == 0;
    }

    private static bool IsSatisfied(ulong bits, ulong pattern, uint waitMode) =>
        (waitMode & 0x0F) == WaitAnd
            ? (bits & pattern) == pattern
            : (bits & pattern) != 0;

    private static void ApplyClearMode(EventFlagState state, ulong pattern, uint waitMode)
    {
        switch (waitMode & 0xF0)
        {
            case ClearAll:
                state.Bits = 0;
                break;
            case ClearPattern:
                state.Bits &= ~pattern;
                break;
        }
    }

    private static bool TryCompleteSatisfiedWait(
    CpuContext ctx,
    EventFlagState state,
    ulong pattern,
    uint waitMode,
    ulong resultAddress,
    out OrbisGen2Result result)
    {
        result = OrbisGen2Result.ORBIS_GEN2_OK;

        if (!IsSatisfied(state.Bits, pattern, waitMode))
        {
            return false;
        }

        if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
        {
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT;
            return true;
        }

        ApplyClearMode(state, pattern, waitMode);
        return true;
    }

    private static string GetEventFlagWakeKey(ulong handle) =>
        $"event_flag:0x{handle:X16}";

    internal static int GetWaitingThreadCountForTests(ulong handle)
    {
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return -1;
        }

        lock (state.Gate)
        {
            return state.WaitingThreads;
        }
    }

    private static uint ComputeRemainingMicroseconds(ulong timeoutAddress, uint timeoutUsec, long waitStarted)
    {
        if (timeoutAddress == 0)
        {
            return uint.MaxValue;
        }

        var elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - waitStarted);
        var elapsedUsec = (ulong)Math.Ceiling(elapsedTicks * 1_000_000d / Stopwatch.Frequency);
        return elapsedUsec >= timeoutUsec ? 0 : timeoutUsec - (uint)elapsedUsec;
    }

    private static void WriteRemainingTimeout(CpuContext ctx, ulong timeoutAddress, uint timeoutUsec, long waitStarted)
    {
        if (timeoutAddress != 0)
        {
            _ = TryWriteUInt32(ctx, timeoutAddress, ComputeRemainingMicroseconds(timeoutAddress, timeoutUsec, waitStarted));
        }
    }

    private static bool TryWriteResultPattern(CpuContext ctx, ulong address, ulong bits) =>
        address == 0 || ctx.TryWriteUInt64(address, bits);

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int capacity, out string value)
    {
        var bytes = new byte[capacity];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, current))
            {
                value = string.Empty;
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, index);
                return true;
            }

            bytes[index] = current[0];
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    private static void TraceEventFlag(string message)
    {
        if (_traceEventFlag)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] event_flag.{message}");
        }
    }

    private static ulong GetCurrentReturnRip() =>
        GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0UL;

    private static string FormatFrameChain(CpuContext ctx)
    {
        Span<ulong> returns = stackalloc ulong[4];
        var count = 0;
        var frame = ctx[CpuRegister.Rbp];
        for (var index = 0; index < returns.Length && frame != 0; index++)
        {
            if (!ctx.TryReadUInt64(frame, out var nextFrame) ||
                !ctx.TryReadUInt64(frame + sizeof(ulong), out var returnAddress))
            {
                break;
            }

            returns[count++] = returnAddress;
            if (nextFrame <= frame)
            {
                break;
            }

            frame = nextFrame;
        }

        return count switch
        {
            0 => "none",
            1 => $"0x{returns[0]:X16}",
            2 => $"0x{returns[0]:X16},0x{returns[1]:X16}",
            3 => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16}",
            _ => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16},0x{returns[3]:X16}",
        };
    }

    private static string FormatGuestWaitObject(CpuContext ctx)
    {
        var r12 = ctx[CpuRegister.R12];
        var r13 = ctx[CpuRegister.R13];
        var objectAddress = r12 != 0
            ? r12
            : r13 >= 0xA8
                ? r13 - 0xA8
                : 0;

        var builder = new StringBuilder(256);
        builder.Append($"r12=0x{r12:X16} r13=0x{r13:X16}");
        if (objectAddress == 0)
        {
            return builder.ToString();
        }

        builder.Append($" obj=0x{objectAddress:X16}");
        AppendUInt32(builder, ctx, objectAddress + 0x58, "o58");
        AppendUInt32(builder, ctx, objectAddress + 0x5C, "o5C");
        AppendUInt64(builder, ctx, objectAddress + 0x60, "o60");
        AppendByte(builder, ctx, objectAddress + 0x6C, "state6C");
        AppendByte(builder, ctx, objectAddress + 0x6D, "o6D");
        AppendByte(builder, ctx, objectAddress + 0xA0, "waitA0");
        AppendByte(builder, ctx, objectAddress + 0xA1, "stateA1");
        AppendByte(builder, ctx, objectAddress + 0xA2, "oA2");
        AppendUInt64(builder, ctx, objectAddress + 0xA8, "eventA8");
        if (r13 != 0)
        {
            AppendUInt64(builder, ctx, r13, "r13_0");
            AppendUInt64(builder, ctx, r13 + 8, "r13_8");
        }

        return builder.ToString();
    }

    private static void AppendByte(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (TryReadByte(ctx, address, out var value))
        {
            builder.Append($" {name}=0x{value:X2}");
        }
    }

    private static void AppendUInt32(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (TryReadUInt32(ctx, address, out var value))
        {
            builder.Append($" {name}=0x{value:X8}");
        }
    }

    private static void AppendUInt64(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (TryReadUInt64(ctx, address, out var value))
        {
            builder.Append($" {name}=0x{value:X16}");
        }
    }
}

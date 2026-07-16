// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.LibcRuntime;

public static class LibcRuntimeCompatExports
{
    private const int MaxGuestStringLength = 64 * 1024;

    [SysAbiExport(
        Nid = "KuOuD58hqn4",
        ExportName = "malloc_stats_fast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MallocStatsFast(CpuContext ctx)
    {
        // Callers provide and initialize the implementation-specific statistics object.
        // Reporting success with its existing zero values is the conservative fallback
        // until SharpEmu's guest allocator exposes compatible statistics.
        return ReturnGuestValue(ctx, 0);
    }

    [SysAbiExport(
        Nid = "YaHc3GS7y7g",
        ExportName = "_Mtx_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MtxInit(CpuContext ctx)
    {
        // Dinkumware passes mutex behavior flags in RSI, not a pthread attribute
        // pointer. The current title uses mode 2, whose required behavior is covered by
        // SharpEmu's default pthread mutex implementation.
        var flags = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rsi] = 0;
        var result = KernelPthreadCompatExports.PosixPthreadMutexInit(ctx);
        ctx[CpuRegister.Rsi] = flags;
        return ReturnGuestValue(ctx, unchecked((ulong)(uint)result));
    }

    [SysAbiExport(
        Nid = "SreZybSRWpU",
        ExportName = "_Cnd_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CndInit(CpuContext ctx)
    {
        var result = KernelPthreadCompatExports.PosixPthreadCondInit(ctx);
        return ReturnGuestValue(ctx, unchecked((ulong)(uint)result));
    }

    [SysAbiExport(
        Nid = "-P6FNMzk2Kc",
        ExportName = "cosf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CosF(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var low, out _);
        var input = BitConverter.Int32BitsToSingle(unchecked((int)(uint)low));
        var result = MathF.Cos(input);
        ctx.SetXmmRegister(0, unchecked((uint)BitConverter.SingleToInt32Bits(result)), 0);
        return ReturnGuestValue(ctx, 0);
    }

    [SysAbiExport(
        Nid = "YQ0navp+YIc",
        ExportName = "puts",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Puts(CpuContext ctx)
    {
        var textAddress = ctx[CpuRegister.Rdi];
        if (!KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx,
                textAddress,
                MaxGuestStringLength,
                out var text))
        {
            return ReturnGuestValue(ctx, unchecked((ulong)(long)-1));
        }

        Console.Out.WriteLine(text);
        Console.Out.Flush();
        return ReturnGuestValue(ctx, unchecked((ulong)(text.Length + Environment.NewLine.Length)));
    }

    [SysAbiExport(
        Nid = "M4YYbSFfJ8g",
        ExportName = "setenv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int SetEnv(CpuContext ctx)
    {
        if (!KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx,
                ctx[CpuRegister.Rdi],
                MaxGuestStringLength,
                out var name) ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx,
                ctx[CpuRegister.Rsi],
                MaxGuestStringLength,
                out var value) ||
            string.IsNullOrEmpty(name) ||
            name.Contains('=', StringComparison.Ordinal))
        {
            return ReturnGuestValue(ctx, ulong.MaxValue);
        }

        var overwrite = ctx[CpuRegister.Rdx] != 0;
        if (overwrite || Environment.GetEnvironmentVariable(name) is null)
        {
            try
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            catch (ArgumentException)
            {
                return ReturnGuestValue(ctx, ulong.MaxValue);
            }
        }

        return ReturnGuestValue(ctx, 0);
    }

    [SysAbiExport(
        Nid = "1Pk0qZQGeWo",
        ExportName = "sscanf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Sscanf(CpuContext ctx)
    {
        if (!KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx,
                ctx[CpuRegister.Rdi],
                MaxGuestStringLength,
                out var input) ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx,
                ctx[CpuRegister.Rsi],
                MaxGuestStringLength,
                out var format))
        {
            return ReturnGuestValue(ctx, ulong.MaxValue);
        }

        var inputIndex = 0;
        var formatIndex = 0;
        var destinationIndex = 0;
        var assigned = 0;
        while (formatIndex < format.Length)
        {
            if (char.IsWhiteSpace(format[formatIndex]))
            {
                while (formatIndex < format.Length && char.IsWhiteSpace(format[formatIndex]))
                {
                    formatIndex++;
                }
                SkipWhiteSpace(input, ref inputIndex);
                continue;
            }

            if (format[formatIndex] != '%')
            {
                if (inputIndex >= input.Length || input[inputIndex] != format[formatIndex])
                {
                    break;
                }

                inputIndex++;
                formatIndex++;
                continue;
            }

            formatIndex++;
            if (formatIndex < format.Length && format[formatIndex] == '%')
            {
                if (inputIndex >= input.Length || input[inputIndex] != '%')
                {
                    break;
                }

                inputIndex++;
                formatIndex++;
                continue;
            }

            var suppressAssignment = formatIndex < format.Length && format[formatIndex] == '*';
            if (suppressAssignment)
            {
                formatIndex++;
            }

            var width = 0;
            while (formatIndex < format.Length && char.IsAsciiDigit(format[formatIndex]))
            {
                width = Math.Min(
                    MaxGuestStringLength,
                    (width * 10) + (format[formatIndex++] - '0'));
            }

            var length = ParseScanLength(format, ref formatIndex);
            if (formatIndex >= format.Length)
            {
                break;
            }

            var specifier = format[formatIndex++];
            if (specifier != 'c' && specifier != 'n')
            {
                SkipWhiteSpace(input, ref inputIndex);
            }

            var destination = suppressAssignment ? 0 : GetScanDestination(ctx, destinationIndex++);
            var converted = specifier switch
            {
                'd' => TryScanInteger(ctx, input, ref inputIndex, width, 10, length, destination, suppressAssignment),
                'i' => TryScanInteger(ctx, input, ref inputIndex, width, 0, length, destination, suppressAssignment),
                'u' => TryScanInteger(ctx, input, ref inputIndex, width, 10, length, destination, suppressAssignment),
                'o' => TryScanInteger(ctx, input, ref inputIndex, width, 8, length, destination, suppressAssignment),
                'x' or 'X' => TryScanInteger(ctx, input, ref inputIndex, width, 16, length, destination, suppressAssignment),
                's' => TryScanString(ctx, input, ref inputIndex, width, destination, suppressAssignment),
                'c' => TryScanCharacters(ctx, input, ref inputIndex, width, destination, suppressAssignment),
                'n' => TryStoreScanInteger(ctx, destination, unchecked((ulong)inputIndex), length, suppressAssignment),
                _ => false,
            };

            if (!converted)
            {
                break;
            }

            if (!suppressAssignment && specifier != 'n')
            {
                assigned++;
            }
        }

        return ReturnGuestValue(ctx, unchecked((ulong)assigned));
    }

    private static string ParseScanLength(string format, ref int index)
    {
        if (index >= format.Length)
        {
            return string.Empty;
        }

        var first = format[index];
        if (first is not ('h' or 'l' or 'j' or 'z' or 't' or 'L'))
        {
            return string.Empty;
        }

        index++;
        if (index < format.Length && format[index] == first && first is 'h' or 'l')
        {
            index++;
            return new string(first, 2);
        }

        return first.ToString();
    }

    private static bool TryScanInteger(
        CpuContext ctx,
        string input,
        ref int inputIndex,
        int width,
        int numberBase,
        string length,
        ulong destination,
        bool suppressAssignment)
    {
        var start = inputIndex;
        var limit = width > 0 ? Math.Min(input.Length, start + width) : input.Length;
        if (inputIndex < limit && input[inputIndex] is '+' or '-')
        {
            inputIndex++;
        }

        var effectiveBase = numberBase;
        if ((effectiveBase is 0 or 16) && inputIndex + 1 < limit &&
            input[inputIndex] == '0' && input[inputIndex + 1] is 'x' or 'X')
        {
            effectiveBase = 16;
            inputIndex += 2;
        }
        else if (effectiveBase == 0)
        {
            effectiveBase = inputIndex < limit && input[inputIndex] == '0' ? 8 : 10;
        }

        var digitStart = inputIndex;
        while (inputIndex < limit && DigitValue(input[inputIndex]) is var digit && digit >= 0 && digit < effectiveBase)
        {
            inputIndex++;
        }

        if (inputIndex == digitStart)
        {
            inputIndex = start;
            return false;
        }

        var token = input.AsSpan(start, inputIndex - start);
        var isNegative = token[0] == '-';
        var digits = token;
        if (digits[0] is '+' or '-')
        {
            digits = digits[1..];
        }
        if (effectiveBase == 16 && digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        ulong value = 0;
        foreach (var character in digits)
        {
            value = unchecked((value * (ulong)effectiveBase) + (ulong)DigitValue(character));
        }
        if (isNegative)
        {
            value = unchecked(0UL - value);
        }

        return TryStoreScanInteger(ctx, destination, value, length, suppressAssignment);
    }

    private static bool TryScanString(
        CpuContext ctx,
        string input,
        ref int inputIndex,
        int width,
        ulong destination,
        bool suppressAssignment)
    {
        var start = inputIndex;
        var limit = width > 0 ? Math.Min(input.Length, start + width) : input.Length;
        while (inputIndex < limit && !char.IsWhiteSpace(input[inputIndex]))
        {
            inputIndex++;
        }
        if (inputIndex == start)
        {
            return false;
        }
        if (suppressAssignment)
        {
            return true;
        }
        if (destination == 0)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(input.Substring(start, inputIndex - start));
        return ctx.Memory.TryWrite(destination, bytes) &&
            ctx.Memory.TryWrite(destination + unchecked((ulong)bytes.Length), stackalloc byte[] { 0 });
    }

    private static bool TryScanCharacters(
        CpuContext ctx,
        string input,
        ref int inputIndex,
        int width,
        ulong destination,
        bool suppressAssignment)
    {
        var count = width > 0 ? width : 1;
        if (inputIndex + count > input.Length)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(input.Substring(inputIndex, count));
        inputIndex += count;
        return suppressAssignment || (destination != 0 && ctx.Memory.TryWrite(destination, bytes));
    }

    private static bool TryStoreScanInteger(
        CpuContext ctx,
        ulong destination,
        ulong value,
        string length,
        bool suppressAssignment)
    {
        if (suppressAssignment)
        {
            return true;
        }
        if (destination == 0)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        var size = length switch
        {
            "hh" => sizeof(byte),
            "h" => sizeof(ushort),
            "l" or "ll" or "j" or "z" or "t" => sizeof(ulong),
            _ => sizeof(uint),
        };
        switch (size)
        {
            case sizeof(byte):
                bytes[0] = unchecked((byte)value);
                break;
            case sizeof(ushort):
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, unchecked((ushort)value));
                break;
            case sizeof(uint):
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)value));
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
                break;
        }

        return ctx.Memory.TryWrite(destination, bytes[..size]);
    }

    private static ulong GetScanDestination(CpuContext ctx, int index) => index switch
    {
        0 => ctx[CpuRegister.Rdx],
        1 => ctx[CpuRegister.Rcx],
        2 => ctx[CpuRegister.R8],
        3 => ctx[CpuRegister.R9],
        _ => ctx.TryReadUInt64(
            ctx[CpuRegister.Rsp] + sizeof(ulong) + unchecked((ulong)(index - 4) * sizeof(ulong)),
            out var destination)
                ? destination
                : 0,
    };

    private static void SkipWhiteSpace(string input, ref int index)
    {
        while (index < input.Length && char.IsWhiteSpace(input[index]))
        {
            index++;
        }
    }

    private static int DigitValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1,
    };

    private static int ReturnGuestValue(CpuContext ctx, ulong value)
    {
        ctx[CpuRegister.Rax] = value;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}

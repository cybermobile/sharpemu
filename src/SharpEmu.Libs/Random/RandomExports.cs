// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Random;

public static class RandomExports
{
    internal const int MaximumRequestSize = 64;
    internal const int RandomErrorInvalid = unchecked((int)0x817C0016);

    [SysAbiExport(
        Nid = "PI7jIZj4pcE",
        ExportName = "sceRandomGetRandomNumber",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRandom")]
    public static int RandomGetRandomNumber(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var requestedSize = ctx[CpuRegister.Rsi];
        if (destinationAddress == 0 || requestedSize > MaximumRequestSize)
        {
            return RandomErrorInvalid;
        }

        Span<byte> randomBytes = stackalloc byte[checked((int)requestedSize)];
        RandomNumberGenerator.Fill(randomBytes);
        if (!ctx.Memory.TryWrite(destinationAddress, randomBytes))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}

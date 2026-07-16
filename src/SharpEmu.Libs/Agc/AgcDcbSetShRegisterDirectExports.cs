// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // Translated from Kyty's Graphics::DcbSetShRegisterDirect implementation;
    // see NOTICE-Kyty.md for the pinned revision and retained MIT notice.
    [SysAbiExport(
        Nid = "pFLArOT53+w",
        ExportName = "sceAgcDcbSetShRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegisterDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var packedRegister = ctx[CpuRegister.Rsi];
        var registerOffset = (uint)packedRegister;
        var registerValue = (uint)(packedRegister >> 32);
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerOffset & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 8, registerValue))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_set_sh_register_direct buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{registerOffset:X8} value=0x{registerValue:X8}");
        return ReturnPointer(ctx, commandAddress);
    }
}

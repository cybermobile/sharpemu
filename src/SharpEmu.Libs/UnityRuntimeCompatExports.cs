// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.UnityRuntime;

public static class UnityRuntimeCompatExports
{
    [SysAbiExport(
        Nid = "35NoyMOtYpE",
        ExportName = "SetDataFolder",
        Target = Generation.Gen5,
        LibraryName = "Unity")]
    public static int SetDataFolder(CpuContext ctx) => ReturnGuestVoid(ctx);

    [SysAbiExport(
        Nid = "cJ2Y4E-t258",
        ExportName = "il2cpp_api_register_symbols",
        Target = Generation.Gen5,
        LibraryName = "Unity")]
    public static int Il2CppApiRegisterSymbols(CpuContext ctx)
    {
        // Unity invokes this process-level initializer without arguments before opening
        // its IL2CPP modules. Runtime symbols still come from the loaded module images;
        // an encrypted module cannot be reconstructed by this initializer.
        return ReturnGuestVoid(ctx);
    }

    [SysAbiExport(
        Nid = "-pnj3-7a6QA",
        ExportName = "unity_mono_set_user_malloc_mutex",
        Target = Generation.Gen5,
        LibraryName = "Unity")]
    public static int UnityMonoSetUserMallocMutex(CpuContext ctx) => ReturnGuestVoid(ctx);

    private static int ReturnGuestVoid(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}

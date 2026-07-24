// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Recognizes the Intel SHA transform loop emitted by the guest's crypto
/// library. The surrounding scalar setup and tail remain guest code; only the
/// complete-block compression loop is replaced.
/// </summary>
internal static class Sha1TransformLoopFastPath
{
    public const ulong ResumeDelta = 0x214;
    public const ulong LoopExitSignatureDelta = 0x1EC;

    private static ReadOnlySpan<byte> EntrySignature =>
    [
        0x41, 0x0F, 0x38, 0xC9, 0xF8, // sha1msg1 xmm7,xmm8
        0x0F, 0x3A, 0xCC, 0xF5, 0x00, // sha1rnds4 xmm6,xmm5,0
        0xC5, 0xF9, 0x6F, 0xE8,       // vmovdqa xmm5,xmm0
        0x41, 0x0F, 0x38, 0xC8, 0xE8 // sha1nexte xmm5,xmm8
    ];

    private static ReadOnlySpan<byte> ExitSignature =>
    [
        0x48, 0x83, 0xF9, 0x3F,             // cmp rcx,63
        0x0F, 0x87, 0xCF, 0xFD, 0xFF, 0xFF, // ja loop
        0x48, 0x89, 0xD1,                   // mov rcx,rdx
        0xC5, 0xF9, 0x70, 0xC0, 0x1B       // vpshufd xmm0,xmm0,1b
    ];

    public static int EntrySignatureLength => EntrySignature.Length;

    public static int ExitSignatureLength => ExitSignature.Length;

    public static bool Matches(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> loopExit) =>
        entry.StartsWith(EntrySignature) && loopExit.StartsWith(ExitSignature);
}

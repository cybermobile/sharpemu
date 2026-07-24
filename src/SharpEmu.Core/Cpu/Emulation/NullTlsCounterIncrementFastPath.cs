// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Recognizes a runtime telemetry helper that increments a counter through a
/// pointer stored in static TLS. Some titles leave the optional pointer null
/// when their platform pthread startup is provided by HLE. The global counter
/// is updated before this helper is called, so skipping only the per-thread
/// increment preserves execution without manufacturing guest-owned storage.
/// </summary>
internal static class NullTlsCounterIncrementFastPath
{
    internal const int SignaturePrefixLength = 9;
    internal const int InstructionLength = 4;
    internal const int SignatureLength = SignaturePrefixLength + InstructionLength;

    private static ReadOnlySpan<byte> Signature =>
    [
        0x89, 0xD9,                         // mov ecx,ebx
        0x48, 0x8B, 0x80, 0x28, 0xFD, 0xFF, 0xFF, // mov rax,[rax-0x2D8]
        0xF0, 0xFF, 0x04, 0x88,             // lock inc dword ptr [rax+rcx*4]
    ];

    internal static bool Matches(ReadOnlySpan<byte> bytes) => bytes.SequenceEqual(Signature);
}

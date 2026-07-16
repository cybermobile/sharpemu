// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint GdsSizeBytes = 64 * 1024;
        private const uint M0ScalarRegister = 124;
        private const uint DeviceScope = 1;
        private const uint AcquireReleaseUniformMemorySemantics = 0x48;

        private bool TryEmitGds(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (_gdsBufferIndex < 0 || _globalBuffers == 0 ||
                _storageUintPointer == 0)
            {
                error = "GDS storage buffer is not declared";
                return false;
            }

            if (instruction.Opcode is not ("DsAppend" or "DsConsume") ||
                instruction.Destinations.Count != 1 ||
                instruction.Control is not Gen5DataShareControl control)
            {
                error = $"unsupported GDS instruction {instruction.Opcode}";
                return false;
            }

            var m0 = LoadS(M0ScalarRegister);
            var sizeBytes = BitwiseAnd(m0, UInt(0xFFFF));
            var baseBytes = ShiftRightLogical(m0, UInt(16));
            var offsetBytes = (control.Offset1 << 8) | control.Offset0;

            // RDNA stores the GDS base in M0[31:16] in bytes; DS offsets are
            // bytes too. The storage-buffer view is a uint array, so convert
            // the complete byte address only after applying the instruction offset.
            var byteAddress = IAdd(
                baseBytes,
                UInt(offsetBytes));
            var counterIndex = ShiftRightLogical(byteAddress, UInt(2));
            var withinM0Size = _module.AddInstruction(
                SpirvOp.ULessThanEqual,
                _boolType,
                UInt(offsetBytes + sizeof(uint)),
                sizeBytes);
            var withinAllocation = _module.AddInstruction(
                SpirvOp.ULessThanEqual,
                _boolType,
                IAdd(byteAddress, UInt(sizeof(uint))),
                UInt(GdsSizeBytes));
            var aligned = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                BitwiseAnd(byteAddress, UInt(sizeof(uint) - 1)),
                UInt(0));
            var addressValid = LogicalAnd(
                withinM0Size,
                LogicalAnd(withinAllocation, aligned));
            var destination = instruction.Destinations[0].Value;

            if (_subgroupInvocationIdInput == 0)
            {
                EmitConditional(
                    LogicalAnd(addressValid, LoadEffectiveExec()),
                    () =>
                    {
                        var previous = EmitGdsAtomic(
                            instruction.Opcode,
                            counterIndex,
                            UInt(1));
                        StoreV(destination, previous, guardWithExec: false);
                        EmitGdsMemoryBarrier();
                    });
                return true;
            }

            if (_emulateWave64)
            {
                EmitWave64GdsAtomic(
                    instruction.Opcode,
                    counterIndex,
                    destination,
                    addressValid);
                return true;
            }

            EmitSubgroupGdsAtomic(
                instruction.Opcode,
                counterIndex,
                destination,
                addressValid);
            return true;
        }

        private void EmitSubgroupGdsAtomic(
            string opcode,
            uint counterIndex,
            uint destination,
            uint addressValid)
        {
            var activeBallot = _module.AddInstruction(
                SpirvOp.GroupNonUniformBallot,
                _uvec4Type,
                UInt(3),
                LoadEffectiveExec());
            var activeMask = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _uintType,
                activeBallot,
                0);
            var activeCount = _module.AddInstruction(
                SpirvOp.BitCount,
                _uintType,
                activeMask);
            var anyActive = IsNotZero(activeMask);
            var firstActiveLane = Ext(73, _uintType, activeMask);
            var isFirstActive = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                Load(_uintType, _subgroupInvocationIdInput),
                firstActiveLane);

            EmitConditional(LogicalAnd(addressValid, anyActive), () =>
            {
                EmitConditional(isFirstActive, () =>
                    Store(
                        _gdsAtomicResult,
                        EmitGdsAtomic(opcode, counterIndex, activeCount)));
                EmitGdsMemoryBarrier();
                var previous = _module.AddInstruction(
                    SpirvOp.GroupNonUniformBroadcast,
                    _uintType,
                    UInt(3),
                    Load(_uintType, _gdsAtomicResult),
                    firstActiveLane);
                EmitExecConditional(() =>
                    StoreV(destination, previous, guardWithExec: false));
            });
        }

        private void EmitWave64GdsAtomic(
            string opcode,
            uint counterIndex,
            uint destination,
            uint addressValid)
        {
            var activeMask = BooleanToWaveMask(LoadEffectiveExec());
            var lowMask = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                activeMask);
            var highMask = _module.AddInstruction(
                SpirvOp.UConvert,
                _uintType,
                ShiftRightLogical64(
                    activeMask,
                    _module.Constant64(_ulongType, 32)));
            var activeCount = IAdd(
                _module.AddInstruction(SpirvOp.BitCount, _uintType, lowMask),
                _module.AddInstruction(SpirvOp.BitCount, _uintType, highMask));
            var hasLow = IsNotZero(lowMask);
            var hasHigh = IsNotZero(highMask);
            var anyActive = _module.AddInstruction(
                SpirvOp.LogicalOr,
                _boolType,
                hasLow,
                hasHigh);
            var firstActiveLane = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                hasLow,
                Ext(73, _uintType, lowMask),
                IAdd(UInt(32), Ext(73, _uintType, highMask)));
            var isFirstActive = _module.AddInstruction(
                SpirvOp.IEqual,
                _boolType,
                GuestWaveLane(),
                firstActiveLane);

            EmitConditional(LogicalAnd(addressValid, anyActive), () =>
            {
                EmitConditional(isFirstActive, () =>
                    Store(
                        WaveBroadcastScratchPointer(),
                        EmitGdsAtomic(opcode, counterIndex, activeCount)));
                EmitGdsMemoryBarrier();
                EmitWave64Barrier();
                var previous = Load(
                    _uintType,
                    WaveBroadcastScratchPointer());
                EmitExecConditional(() =>
                    StoreV(destination, previous, guardWithExec: false));
                EmitWave64Barrier();
            });
        }

        private uint EmitGdsAtomic(
            string opcode,
            uint counterIndex,
            uint count) =>
            _module.AddInstruction(
                opcode == "DsAppend" ? SpirvOp.AtomicIAdd : SpirvOp.AtomicISub,
                _uintType,
                BufferWordPointer(_gdsBufferIndex, counterIndex),
                UInt(DeviceScope),
                UInt(AcquireReleaseUniformMemorySemantics),
                count);

        private void EmitGdsMemoryBarrier() =>
            _module.AddStatement(
                SpirvOp.MemoryBarrier,
                UInt(DeviceScope),
                UInt(AcquireReleaseUniformMemorySemantics));

        private uint LogicalAnd(uint left, uint right) =>
            _module.AddInstruction(
                SpirvOp.LogicalAnd,
                _boolType,
                left,
                right);
    }
}

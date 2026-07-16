// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu;

public sealed class Gen5GdsShaderTests
{
    private const ulong ProgramAddress = 0x10_0000;

    [Theory]
    [InlineData(0xD8FA000Cu, "DsAppend", 5u)]
    [InlineData(0xD8F6000Cu, "DsConsume", 6u)]
    public void DecodeGdsCounterInstruction_UsesM0AndReturnsOneVector(
        uint word,
        string opcode,
        uint destination)
    {
        var program = Decode(word, destination << 24, 0xBF810000u);

        var instruction = Assert.Single(program.Instructions.Take(1));
        Assert.Equal(Gen5ShaderEncoding.Ds, instruction.Encoding);
        Assert.Equal(opcode, instruction.Opcode);
        Assert.Empty(instruction.Sources);
        var output = Assert.Single(instruction.Destinations);
        Assert.Equal(Gen5OperandKind.VectorRegister, output.Kind);
        Assert.Equal(destination, output.Value);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.True(control.Gds);
        Assert.Equal(12u, control.Offset0);
        Assert.Equal(0u, control.Offset1);
    }

    [Theory]
    [InlineData(0xD8FA000Cu, 234u)]
    [InlineData(0xD8F6000Cu, 235u)]
    public void CompileGdsCounterInstruction_EmitsAppendedDeviceAtomicUnderExec(
        uint word,
        uint expectedAtomicOpcode)
    {
        var program = Decode(word, 5u << 24, 0xBF810000u);
        var initialScalars = new uint[256];
        initialScalars[124] = 0x0020_0100u;
        var state = new Gen5ShaderState(program, [], Metadata: null);
        var evaluation = new Gen5ShaderEvaluation(
            initialScalars,
            new uint[256],
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error,
                totalGlobalBufferCount: 7),
            error);
        Assert.True(shader.UsesGds);

        var module = SpirvModule.Parse(shader.Spirv);
        var atomic = Assert.Single(module.WithOpcode(expectedAtomicOpcode));
        Assert.Equal(1u, module.Constant(atomic.Operands[3]));
        Assert.Equal(0x48u, module.Constant(atomic.Operands[4]));
        Assert.Equal(205u, module.ByResultId(atomic.Operands[5]).Opcode); // OpBitCount.

        var pointer = module.ByResultId(atomic.Operands[2]);
        Assert.Equal(65u, pointer.Opcode); // OpAccessChain.
        var guestBuffers = module.NamedId("guestBuffers");
        Assert.Equal(guestBuffers, pointer.Operands[2]);
        Assert.Equal(7u, module.Constant(pointer.Operands[3]));
        Assert.Equal(0u, module.Constant(pointer.Operands[4]));
        var dwordIndex = module.ByResultId(pointer.Operands[5]);
        Assert.Equal(194u, dwordIndex.Opcode); // OpShiftRightLogical.
        Assert.Equal(2u, module.MaskedShiftConstant(dwordIndex.Operands[3]));
        var byteAddress = module.ByResultId(dwordIndex.Operands[2]);
        Assert.Equal(128u, byteAddress.Opcode); // OpIAdd.
        Assert.Equal(12u, module.Constant(byteAddress.Operands[3]));

        var barrier = Assert.Single(module.WithOpcode(225));
        Assert.Equal(1u, module.Constant(barrier.Operands[0]));
        Assert.Equal(0x48u, module.Constant(barrier.Operands[1]));
        Assert.NotEmpty(module.WithOpcode(250)); // OpBranchConditional (EXEC guard).
        Assert.NotEmpty(module.WithOpcode(339)); // OpGroupNonUniformBallot.
        Assert.NotEmpty(module.WithOpcode(337)); // OpGroupNonUniformBroadcast.
        Assert.True(module.WithOpcode(178).Count() >= 2); // OpULessThanEqual bounds.

        Assert.Contains(
            module.WithOpcode(28),
            typeArray => module.Constant(typeArray.Operands[2]) == 8u);
        Assert.False(module.HasName("lds"));
    }

    [Fact]
    public void GdsOnlyComputeProgram_ReachesBackendAndCarriesBindingMetadata()
    {
        Assert.True(AgcExports.ShouldTranslateComputeProgram(
            usesGds: true,
            hasStorageBinding: false,
            writesGlobalMemory: false,
            localSizeX: 32,
            localSizeY: 1,
            localSizeZ: 1));
        Assert.False(AgcExports.ShouldTranslateComputeProgram(
            usesGds: false,
            hasStorageBinding: false,
            writesGlobalMemory: false,
            localSizeX: 32,
            localSizeY: 1,
            localSizeZ: 1));

        var program = Decode(0xD8FA0000u, 5u << 24, 0xBF810000u);
        var initialScalars = new uint[256];
        initialScalars[124] = 0x0000_0100u;
        var backend = new VulkanGuestGpuBackend();
        Assert.True(backend.TryCompileComputeShader(
            new Gen5ShaderState(program, [], Metadata: null),
            new Gen5ShaderEvaluation(initialScalars, new uint[256], [], []),
            32,
            1,
            1,
            out var shader,
            out var error,
            totalGlobalBufferCount: 3), error);
        Assert.True(Assert.IsType<VulkanCompiledGuestShader>(shader).UsesGds);
    }

    [Fact]
    public void ExactWave64PartialDispatch_KeepsBarrierParticipantsAndMasksExec()
    {
        var program = Decode(
            0xBEFE03C1u, // s_mov_b32 exec_lo, -1
            0xBEFF03C1u, // s_mov_b32 exec_hi, -1
            0xD8FA0000u,
            5u << 24,
            0xBF810000u);
        var initialScalars = new uint[256];
        initialScalars[124] = 0x0000_0100u;

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                new Gen5ShaderState(program, [], Metadata: null),
                new Gen5ShaderEvaluation(initialScalars, new uint[256], [], []),
                64,
                1,
                1,
                out var shader,
                out var error,
                waveLaneCount: 64),
            error);

        var module = SpirvModule.Parse(shader.Spirv);
        var programActive = module.NamedId("programActive");
        var invocationInBounds = module.NamedId("computeInvocationInBounds");
        var exec = module.NamedId("exec");
        var boundsStore = Assert.Single(
            module.WithOpcode(62),
            store => store.Operands[0] == invocationInBounds); // OpStore.
        var boundsValue = boundsStore.Operands[1];

        // Clipped lanes must remain in the dispatcher so they reach every
        // workgroup barrier used to bridge the two host subgroups.
        Assert.DoesNotContain(
            module.WithOpcode(62).Where(store =>
                store.Operands[0] == programActive),
            store => store.Operands[1] == boundsValue);
        Assert.True(module.WithOpcode(224).Count() >= 2); // OpControlBarrier.

        // Guest-visible work still uses EXEC intersected with the exact
        // dispatch bounds, even if the guest later widens its raw EXEC mask.
        var boundsLoads = module.LoadResultIds(invocationInBounds);
        var execLoads = module.LoadResultIds(exec);
        Assert.Contains(
            module.WithOpcode(167), // OpLogicalAnd.
            instruction =>
                instruction.Operands.Skip(2).Any(boundsLoads.Contains) &&
                instruction.Operands.Skip(2).Any(execLoads.Contains));
        Assert.Single(module.WithOpcode(234)); // One OpAtomicIAdd.
    }

    [Fact]
    public void MultiWaveWorkgroup_Wave64GdsIsRejectedUntilScratchIsPerWave()
    {
        var program = Decode(0xD8FA0000u, 5u << 24, 0xBF810000u);
        var initialScalars = new uint[256];
        initialScalars[124] = 0x0000_0100u;

        Assert.False(Gen5SpirvTranslator.TryCompileComputeShader(
            new Gen5ShaderState(program, [], Metadata: null),
            new Gen5ShaderEvaluation(initialScalars, new uint[256], [], []),
            128,
            1,
            1,
            out _,
            out var error,
            waveLaneCount: 64));
        Assert.Contains("exactly 64", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 64u, false, 0u)]
    [InlineData(true, 32u, false, 0u)]
    [InlineData(true, 64u, true, 32u)]
    public void GdsComputePipeline_RequiresA32LanePhysicalSubgroup(
        bool usesGds,
        uint defaultSubgroupSize,
        bool canRequireSize32,
        uint expectedRequiredSize)
    {
        Assert.Equal(
            expectedRequiredSize,
            VulkanVideoPresenter.GetRequiredComputeSubgroupSize(
                usesGds,
                defaultSubgroupSize,
                canRequireSize32));
    }

    [Fact]
    public void GdsComputePipeline_RejectsAnUncontrollableNon32LaneSubgroup()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            VulkanVideoPresenter.GetRequiredComputeSubgroupSize(
                usesGds: true,
                defaultSubgroupSize: 64,
                canRequireSize32: false));

        Assert.Contains("32-lane", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GdsComputePipeline_RejectsMissingSubgroupOperations()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            VulkanVideoPresenter.GetRequiredComputeSubgroupSize(
                usesGds: true,
                defaultSubgroupSize: 32,
                canRequireSize32: false,
                supportsGdsSubgroupOperations: false));

        Assert.Contains("basic and ballot", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeResourcePropagation_AppendsGdsExactlyOnce()
    {
        Assert.Equal(7, VulkanVideoPresenter.GetGlobalBufferResourceCount(7, false));
        Assert.Equal(8, VulkanVideoPresenter.GetGlobalBufferResourceCount(7, true));
    }

    [Fact]
    public void GraphicsStageGds_IsRejectedUntilWaveAggregationIsImplemented()
    {
        var program = Decode(0xD8FA0000u, 5u << 24, 0xBF810000u);
        var state = new Gen5ShaderState(program, [], Metadata: null);
        var evaluation = new Gen5ShaderEvaluation(
            new uint[256],
            new uint[256],
            [],
            []);

        Assert.False(Gen5SpirvTranslator.TryCompileVertexShader(
            state,
            evaluation,
            out _,
            out var vertexError));
        Assert.False(Gen5SpirvTranslator.TryCompilePixelShader(
            state,
            evaluation,
            Gen5PixelOutputKind.Float,
            out _,
            out var pixelError));
        Assert.Contains("only in compute", vertexError, StringComparison.Ordinal);
        Assert.Contains("only in compute", pixelError, StringComparison.Ordinal);
    }

    private static Gen5ShaderProgram Decode(params uint[] words)
    {
        var memory = new FakeCpuMemory(ProgramAddress, words.Length * sizeof(uint));
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(encoded, words[index]);
            Assert.True(memory.TryWrite(
                ProgramAddress + (ulong)(index * sizeof(uint)),
                encoded));
        }

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private sealed class SpirvModule
    {
        private readonly List<Instruction> _instructions;
        private readonly Dictionary<uint, uint> _constants;
        private readonly Dictionary<uint, string> _names;

        private SpirvModule(
            List<Instruction> instructions,
            Dictionary<uint, uint> constants,
            Dictionary<uint, string> names)
        {
            _instructions = instructions;
            _constants = constants;
            _names = names;
        }

        public static SpirvModule Parse(byte[] bytes)
        {
            var words = new uint[bytes.Length / sizeof(uint)];
            for (var index = 0; index < words.Length; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(index * sizeof(uint), sizeof(uint)));
            }

            var instructions = new List<Instruction>();
            var constants = new Dictionary<uint, uint>();
            var names = new Dictionary<uint, string>();
            for (var cursor = 5; cursor < words.Length;)
            {
                var wordCount = (int)(words[cursor] >> 16);
                Assert.True(wordCount > 0);
                var opcode = words[cursor] & 0xFFFFu;
                var operands = words.AsSpan(cursor + 1, wordCount - 1).ToArray();
                var instruction = new Instruction(opcode, operands);
                instructions.Add(instruction);
                if (opcode == 43 && operands.Length >= 3) // OpConstant.
                {
                    constants[operands[1]] = operands[2];
                }
                else if (opcode == 5 && operands.Length >= 2) // OpName.
                {
                    names[operands[0]] = DecodeString(operands.AsSpan(1));
                }

                cursor += wordCount;
            }

            return new SpirvModule(instructions, constants, names);
        }

        public IEnumerable<Instruction> WithOpcode(uint opcode) =>
            _instructions.Where(instruction => instruction.Opcode == opcode);

        public uint Constant(uint id) => _constants[id];

        public uint NamedId(string name) =>
            _names.Single(entry => entry.Value == name).Key;

        public bool HasName(string name) => _names.ContainsValue(name);

        public HashSet<uint> LoadResultIds(uint pointer) =>
            WithOpcode(61) // OpLoad.
                .Where(instruction => instruction.Operands[2] == pointer)
                .Select(instruction => instruction.Operands[1])
                .ToHashSet();

        public Instruction ByResultId(uint id) =>
            _instructions.Single(instruction =>
                instruction.Operands.Length >= 2 && instruction.Operands[1] == id);

        public uint MaskedShiftConstant(uint id)
        {
            var mask = ByResultId(id);
            Assert.Equal(199u, mask.Opcode); // OpBitwiseAnd.
            Assert.Equal(31u, Constant(mask.Operands[3]));
            return Constant(mask.Operands[2]);
        }

        private static string DecodeString(ReadOnlySpan<uint> words)
        {
            Span<byte> bytes = stackalloc byte[words.Length * sizeof(uint)];
            for (var index = 0; index < words.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes[(index * sizeof(uint))..],
                    words[index]);
            }

            var terminator = bytes.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(
                terminator < 0 ? bytes : bytes[..terminator]);
        }
    }

    private sealed record Instruction(uint Opcode, uint[] Operands);
}

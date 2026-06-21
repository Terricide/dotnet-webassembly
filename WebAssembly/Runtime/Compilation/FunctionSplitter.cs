using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using WebAssembly.Instructions;

namespace WebAssembly.Runtime.Compilation;

/// <summary>
/// Splits oversized function bodies so the .NET JIT can fully optimize them.
///
/// The JIT abandons full optimization ("switched to MinOpts") for methods whose IL exceeds an
/// internal size limit (~60 KB), which disables register allocation and inlining — a large
/// slowdown. Emscripten/LLVM <c>-O3</c> routinely produces single wasm functions far past that.
///
/// This lifts stack-isolated, branch-contained <c>block</c>/<c>loop</c> regions out of such a
/// function into separate helper methods, each small enough to be fully optimized. A region is
/// outlinable when it is a void block/loop (so its operand stack is empty at entry and exit, by
/// the wasm spec) and no branch inside it targets a label outside it and it contains no
/// <c>return</c>. The locals the region uses are passed to the helper by reference; the helper
/// also receives the <c>CompiledExports</c> instance for linear-memory access.
/// </summary>
internal static class FunctionSplitter
{
    /// <summary>A region selected for outlining and the compiled helper that replaces it.</summary>
    internal sealed class OutlinedRegion(int start, int end, uint[] usedLocals, MethodInfo child)
    {
        public int Start { get; } = start;          // index of the Block/Loop instruction
        public int End { get; } = end;              // index of the matching End
        public uint[] UsedLocals { get; } = usedLocals;
        public MethodInfo Child { get; } = child;
    }

    /// <summary>
    /// If <paramref name="instructions"/> is oversized and splitting is enabled, compiles helper
    /// methods for selected regions and returns a rewritten instruction/offset stream in which each
    /// outlined region is replaced by a single <see cref="CallOutlinedRegion"/> placeholder.
    /// Returns false (leaving the streams untouched) when nothing is outlined.
    /// </summary>
    public static bool TrySplit(
        CompilationContext context,
        TypeBuilder exportsBuilder,
        CompilerConfiguration configuration,
        uint parentFunctionIndex,
        WebAssemblyValueType[] combinedLocals,
        ref List<Instruction> instructions,
        ref List<long> instructionOffsets)
    {
        if (!configuration.EnableFunctionSplitting)
            return false;

        var threshold = configuration.FunctionSplitInstructionThreshold;
        var minRegion = configuration.FunctionSplitMinimumRegionInstructions;
        if (instructions.Count <= threshold)
            return false;

        var regions = BuildRegions(instructions);
        var selected = SelectRegions(regions, instructions.Count, threshold, minRegion);
        if (selected.Count == 0)
            return false;

        // Compile a helper for each selected region. This reuses `context`; the caller re-resets it
        // for the parent function afterward. Helpers are compiled in source order; selection
        // guarantees they are disjoint.
        var outlined = new List<OutlinedRegion>(selected.Count);
        var regionId = 0;
        foreach (var region in selected.OrderBy(r => r.Start))
        {
            var used = CollectUsedLocals(instructions, region.Start, region.End);
            var child = CompileHelper(context, exportsBuilder, configuration, parentFunctionIndex, regionId++,
                instructions, region.Start, region.End, used, combinedLocals);
            outlined.Add(new OutlinedRegion(region.Start, region.End, used, child));
        }

        // Rewrite the parent stream: replace each [Start..End] slice with one placeholder call.
        var newInstructions = new List<Instruction>(instructions.Count);
        var newOffsets = new List<long>(instructions.Count);
        var next = 0;
        foreach (var region in outlined)
        {
            for (; next < region.Start; next++)
            {
                newInstructions.Add(instructions[next]);
                newOffsets.Add(instructionOffsets[next]);
            }
            newInstructions.Add(new CallOutlinedRegion(region.Child, region.UsedLocals));
            newOffsets.Add(instructionOffsets[region.Start]);
            next = region.End + 1;
        }
        for (; next < instructions.Count; next++)
        {
            newInstructions.Add(instructions[next]);
            newOffsets.Add(instructionOffsets[next]);
        }

        instructions = newInstructions;
        instructionOffsets = newOffsets;
        return true;
    }

    private sealed class RegionInfo
    {
        public int Start;
        public int End;
        public int BaseDepth;
        public int Size;
        public bool Eligible;      // void block/loop, no escaping branch, no return inside
        public int ParentIndex = -1;
    }

    private static List<RegionInfo> BuildRegions(List<Instruction> code)
    {
        var regions = new List<RegionInfo>();
        var open = new Stack<RegionInfo>();
        var depth = 0;

        for (var i = 0; i < code.Count; i++)
        {
            switch (code[i])
            {
                case Block block:
                    depth++;
                    open.Push(new RegionInfo { Start = i, BaseDepth = depth, Eligible = IsVoid(block) });
                    break;
                case Loop loop:
                    depth++;
                    open.Push(new RegionInfo { Start = i, BaseDepth = depth, Eligible = IsVoid(loop) });
                    break;
                case If:
                    // `if` carries hidden control flow (the implicit else/branch); not outlined.
                    depth++;
                    open.Push(new RegionInfo { Start = i, BaseDepth = depth, Eligible = false });
                    break;
                case End:
                    if (open.Count > 0)
                    {
                        var region = open.Pop();
                        region.End = i;
                        region.Size = i - region.Start + 1;
                        region.ParentIndex = open.Count > 0 ? open.Peek().Start : -1;
                        regions.Add(region);
                        depth--;
                    }
                    break;
                case Return:
                    foreach (var region in open)
                        region.Eligible = false;
                    break;
                case Branch branch:
                    MarkEscapes(open, depth, branch.Index);
                    break;
                case BranchIf branchIf:
                    MarkEscapes(open, depth, branchIf.Index);
                    break;
                case BranchTable branchTable:
                    foreach (var label in branchTable.Labels)
                        MarkEscapes(open, depth, label);
                    MarkEscapes(open, depth, branchTable.DefaultLabel);
                    break;
            }
        }

        return regions;

        static bool IsVoid(BlockTypeInstruction instruction) =>
            instruction.TypeIndex is null && instruction.Type == BlockType.Empty;

        static void MarkEscapes(Stack<RegionInfo> open, int depth, uint index)
        {
            var targetDepth = depth - (int)index;
            foreach (var region in open)
                if (targetDepth < region.BaseDepth)
                    region.Eligible = false;
        }
    }

    /// <summary>
    /// Top-down selection: outline each eligible region that is itself within the size threshold,
    /// without descending into one already chosen. This yields disjoint regions each small enough to
    /// be fully optimized. Only applied if the residual parent also lands within the threshold, so
    /// splitting never adds call overhead without removing the MinOpts penalty.
    /// </summary>
    private static List<RegionInfo> SelectRegions(List<RegionInfo> regions, int totalInstructions, int threshold, int minRegion)
    {
        var byStart = regions.OrderBy(r => r.Start).ToList();
        var selected = new List<RegionInfo>();
        var coveredUntil = -1;
        foreach (var region in byStart)
        {
            if (region.Start <= coveredUntil)
                continue; // inside an already-selected region
            if (!region.Eligible || region.Size < minRegion || region.Size > threshold)
                continue;
            selected.Add(region);
            coveredUntil = region.End;
        }

        var extracted = selected.Sum(r => r.Size - 1); // each region becomes one placeholder call
        if (totalInstructions - extracted > threshold)
            return []; // insufficient coverage — leave the function intact

        return selected;
    }

    private static uint[] CollectUsedLocals(List<Instruction> code, int start, int end)
    {
        var used = new SortedSet<uint>();
        for (var i = start; i <= end; i++)
        {
            switch (code[i])
            {
                case LocalGet get: used.Add(get.Index); break;
                case LocalSet set: used.Add(set.Index); break;
                case LocalTee tee: used.Add(tee.Index); break;
            }
        }
        return used.ToArray();
    }

    private static MethodBuilder CompileHelper(
        CompilationContext context,
        TypeBuilder exportsBuilder,
        CompilerConfiguration configuration,
        uint parentFunctionIndex,
        int regionId,
        List<Instruction> code,
        int start,
        int end,
        uint[] usedLocals,
        WebAssemblyValueType[] combinedLocals)
    {
        var parameterTypes = new Type[usedLocals.Length + 1];
        for (var i = 0; i < usedLocals.Length; i++)
            parameterTypes[i] = combinedLocals[usedLocals[i]].ToSystemType().MakeByRefType();
        parameterTypes[usedLocals.Length] = exportsBuilder;

        var child = exportsBuilder.DefineMethod(
            $"🪓 {parentFunctionIndex}_{regionId}",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(void),
            parameterTypes);
        CompilationContext.SetHotPathImplementationFlags(child);

        var il = child.GetILGenerator();

        // The helper is a void method over the parent's locals: use Signature.Empty for a void outer
        // block, but keep the parent's combined locals array so per-local type lookups resolve.
        context.Reset(il, Signature.Empty, combinedLocals);
        context.CurrentFunctionIndex = parentFunctionIndex;

        var map = new Dictionary<uint, int>(usedLocals.Length);
        for (var i = 0; i < usedLocals.Length; i++)
            map[usedLocals[i]] = i;
        context.Outlining = new CompilationContext.OutliningEnvironment(map, usedLocals.Length);

        var regionInstructions = new List<Instruction>(end - start + 1);
        for (var i = start; i <= end; i++)
            regionInstructions.Add(code[i]);

        if (regionInstructions.Any(static instruction => instruction is Call or CallIndirect))
            il.Emit(OpCodes.Call, typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod(nameof(System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack))!);

        if (context.Memory != null
            && regionInstructions.Any(static instruction =>
                instruction is MemoryImmediateInstruction or V128Load or V128Store))
        {
            context.EmitInitMemoryCache();
        }

        foreach (var instruction in regionInstructions)
        {
            instruction.Compile(context);
            context.Previous = instruction.OpCode;
        }

        // The region's trailing End closed only the block frame; close the helper itself.
        il.Emit(OpCodes.Ret);

        context.Outlining = null;
        return child;
    }

    /// <summary>
    /// Synthetic placeholder emitted in a parent function where an outlined region used to be. It
    /// loads the region's used locals by reference, then the instance, and calls the helper.
    /// Stack-neutral: outlined regions are void blocks, so they neither consume nor produce
    /// operand-stack values. Nested (and thus excluded from the public-instruction conventions) and
    /// synthetic — it has no opcode and cannot be serialized.
    /// </summary>
    internal sealed class CallOutlinedRegion(MethodInfo child, uint[] usedLocals) : Instruction
    {
        public override OpCode OpCode => OpCode.NoOperation;

        internal override void WriteTo(Writer writer) =>
            throw new NotSupportedException("Outlined-region placeholders are synthetic and cannot be serialized.");

        public override bool Equals(Instruction? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        internal override void Compile(CompilationContext context)
        {
            var parameterCount = context.CheckedSignature.ParameterTypes.Length;
            foreach (var localIndex in usedLocals)
            {
                if (localIndex < parameterCount)
                {
                    if (localIndex > byte.MaxValue)
                        throw new CompilerException($"Function splitting does not support outlining a parameter at index {localIndex}.");
                    context.Emit(OpCodes.Ldarga_S, (byte)localIndex);
                }
                else
                {
                    context.Emit(OpCodes.Ldloca, context.WasmLocalBuilders[localIndex - parameterCount]);
                }
            }

            context.EmitLoadThis();
            context.Emit(OpCodes.Call, child);
        }
    }
}

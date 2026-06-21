using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using WebAssembly.Runtime;
using WebAssembly.Runtime.Compilation;

namespace WebAssembly.Instructions;

/// <summary>
/// Common features of instructions that access linear memory.
/// </summary>
public abstract class MemoryImmediateInstruction : Instruction, IEquatable<MemoryImmediateInstruction>
{
    /// <summary>
    /// Indicates options for the instruction.
    /// </summary>
    [Flags]
    public enum Options : uint
    {
        /// <summary>
        /// The access uses 8-bit alignment.
        /// </summary>
        Align1 = 0b00,

        /// <summary>
        /// The access uses 16-bit alignment.
        /// </summary>
        Align2 = 0b01,

        /// <summary>
        /// The access uses 32-bit alignment.
        /// </summary>
        Align4 = 0b10,

        /// <summary>
        /// The access uses 64-bit alignment.
        /// </summary>
        Align8 = 0b11,
    }

    /// <summary>
    /// A bitfield which currently contains the alignment in the least significant bits, encoded as log2(alignment).
    /// </summary>
    public Options Flags { get; set; }

    /// <summary>
    /// The index within linear memory for the access operation.
    /// </summary>
    public uint Offset { get; set; }

    private protected MemoryImmediateInstruction()
    {
    }

    private protected MemoryImmediateInstruction(Reader reader)
    {
        Flags = (Options)reader.ReadVarUInt32();
        Offset = reader.ReadVarUInt32();
    }

    internal sealed override void WriteTo(Writer writer)
    {
        writer.Write((byte)this.OpCode);
        writer.WriteVar((uint)this.Flags);
        writer.WriteVar(this.Offset);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => this.Equals(obj as MemoryImmediateInstruction);

    /// <summary>
    /// Determines whether this instruction is identical to another.
    /// </summary>
    /// <param name="other">The instruction to compare against.</param>
    /// <returns>True if they have the same type and value, otherwise false.</returns>
    public override bool Equals(Instruction? other) => this.Equals(other as MemoryImmediateInstruction);

    /// <summary>
    /// Determines whether this instruction is identical to another.
    /// </summary>
    /// <param name="other">The instruction to compare against.</param>
    /// <returns>True if they have the same type and value, otherwise false.</returns>
    public bool Equals(MemoryImmediateInstruction? other) =>
        other != null
        && other.OpCode == this.OpCode
        && other.Flags == this.Flags
        && other.Offset == this.Offset
        ;

    /// <summary>
    /// Returns a simple hash code based on the value of the instruction.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine((int)this.OpCode, (int)this.Flags, (int)this.Offset);

    private protected abstract WebAssemblyValueType Type { get; }

    private protected abstract byte Size { get; }

    private protected abstract System.Reflection.Emit.OpCode EmittedOpCode { get; }

    private protected void ValidateAlignment()
    {
        // Alignment must not be larger than the natural alignment of the type.
        // Flags encodes log2(alignment); natural log2 alignment = log2(Size).
        // Use the raw uint value — the Options enum only covers bits 0-1, but the binary may encode larger values.
        var flagAlignment = (uint)this.Flags;
        var naturalAlignment = this.Size switch { 1 => 0u, 2 => 1u, 4 => 2u, _ => 3u };
        if (flagAlignment > naturalAlignment)
            throw new CompilerException($"Alignment {1u << (int)flagAlignment} is larger than natural alignment {this.Size} for {this.OpCode}.");
    }

    private protected HelperMethod RangeCheckHelper => this.Size switch
    {
        1 => HelperMethod.RangeCheck8,
        2 => HelperMethod.RangeCheck16,
        4 => HelperMethod.RangeCheck32,
        8 => HelperMethod.RangeCheck64,
        _ => throw new InvalidOperationException(),// Shouldn't be possible.
    };

    private protected void EmitRangeCheck(CompilationContext context)
    {
        context.EmitLoadThis();
        context.Emit(OpCodes.Call, context[this.RangeCheckHelper, CreateRangeCheck]);
    }

    /// <summary>
    /// Consumes an address from the IL stack and leaves a bounds-checked native pointer to the
    /// effective address (memory start + address + offset). When the per-function memory cache is
    /// active, the check is a single 64-bit compare against the cached size; otherwise the legacy
    /// range-check helper path is used.
    /// </summary>
    internal static void EmitBoundsCheckedAddress(CompilationContext context, uint offset, byte size, HelperMethod rangeCheckHelper)
    {
        if (context.MemoryCacheActive
            && context.MemoryAddressType == WebAssemblyValueType.Int32
            && offset <= int.MaxValue)
        {
            var address = context.GetScratchLocal(typeof(int), "memAddr");
            var inRange = context.DefineLabel();

            context.Emit(OpCodes.Stloc, address);
            context.Emit(OpCodes.Ldloc, address);
            context.Emit(OpCodes.Conv_U8);
            context.Emit(OpCodes.Ldc_I8, (long)offset + size);
            context.Emit(OpCodes.Add);
            context.Emit(OpCodes.Ldloc, context.MemorySizeLocal!);
            context.Emit(OpCodes.Ble_Un, inRange);

            // Out of range: the helper re-checks against live memory state and throws.
            // The cache is refreshed after every growth opportunity, so it cannot pass here;
            // the call/pop keeps the IL stack consistent with the fast path for the verifier.
            context.Emit(OpCodes.Ldloc, address);
            if (offset != 0)
            {
                Int32Constant.Emit(context, unchecked((int)offset));
                context.Emit(OpCodes.Add_Ovf_Un);
            }
            context.EmitLoadThis();
            context.Emit(OpCodes.Call, context[rangeCheckHelper, CreateRangeCheck]);
            context.Emit(OpCodes.Pop);

            context.MarkLabel(inRange);
            context.Emit(OpCodes.Ldloc, context.MemoryStartLocal!);
            context.Emit(OpCodes.Ldloc, address);
            context.Emit(OpCodes.Conv_U);
            context.Emit(OpCodes.Add);
            if (offset != 0)
            {
                Int32Constant.Emit(context, unchecked((int)offset));
                context.Emit(OpCodes.Conv_U); // offset <= int.MaxValue, so zero- and sign-extension agree
                context.Emit(OpCodes.Add);
            }
            return;
        }

        if (offset != 0)
        {
            if (context.MemoryAddressType == WebAssemblyValueType.Int64)
                context.Emit(OpCodes.Ldc_I8, (long)offset);
            else
                Int32Constant.Emit(context, unchecked((int)offset));
            context.Emit(OpCodes.Add_Ovf_Un);
        }

        if (context.MemoryAddressType == WebAssemblyValueType.Int64)
            context.Emit(OpCodes.Conv_Ovf_U4);

        context.EmitLoadThis();
        context.Emit(OpCodes.Call, context[rangeCheckHelper, CreateRangeCheck]);

        context.EmitLoadThis();
        context.Emit(OpCodes.Ldfld, context.CheckedMemory);
        context.Emit(OpCodes.Ldfld, UnmanagedMemory.StartField);
        context.Emit(OpCodes.Add);
    }

    internal static MethodBuilder CreateRangeCheck(HelperMethod helper, CompilationContext context)
    {
        if (context.Memory == null)
            throw new CompilerException("Cannot use instructions that depend on linear memory when linear memory is not defined.");

        byte size = helper switch
        {
            HelperMethod.RangeCheck8 => 1,
            HelperMethod.RangeCheck16 => 2,
            HelperMethod.RangeCheck32 => 4,
            HelperMethod.RangeCheck64 => 8,
            HelperMethod.RangeCheck128 => 16,
            _ => throw new InvalidOperationException(),
        };

        var builder = context.CheckedExportsBuilder.DefineMethod(
            $"☣ Range Check {size}",
            CompilationContext.HelperMethodAttributes,
            typeof(uint),
            [typeof(uint), context.CheckedExportsBuilder]
            );
        CompilationContext.SetHotPathImplementationFlags(builder, inline: true);
        var il = builder.GetILGenerator();

        void EmitSize()
        {
            if (size <= 8)
                il.Emit(size switch
                {
                    1 => OpCodes.Ldc_I4_1,
                    2 => OpCodes.Ldc_I4_2,
                    4 => OpCodes.Ldc_I4_4,
                    _ => OpCodes.Ldc_I4_8,
                });
            else
                il.Emit(OpCodes.Ldc_I4_S, (sbyte)size);
        }

        var outOfRange = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldfld, context.Memory);
        il.Emit(OpCodes.Ldfld, UnmanagedMemory.SizeField);
        il.Emit(OpCodes.Ldarg_0);
        EmitSize();
        il.Emit(OpCodes.Add_Ovf_Un);
        il.Emit(OpCodes.Blt_Un_S, outOfRange);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(outOfRange);
        il.Emit(OpCodes.Ldarg_0);
        EmitSize();
        il.Emit(OpCodes.Newobj, typeof(MemoryAccessOutOfRangeException)
            .GetTypeInfo()
            .DeclaredConstructors
            .First(c =>
            {
                var parms = c.GetParameters();
                return parms.Length == 2
                && parms[0].ParameterType == typeof(uint)
                && parms[1].ParameterType == typeof(uint)
                ;
            }));
        il.Emit(OpCodes.Throw);
        return builder;
    }

}

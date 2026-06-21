using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using WebAssembly.Runtime;
using WebAssembly.Runtime.Compilation;
using static WebAssembly.Runtime.Compilation.MultiValueHelper;

namespace WebAssembly.Instructions;

/// <summary>
/// Call function indirectly.
/// </summary>
public class CallIndirect : Instruction, IEquatable<CallIndirect>
{
    /// <summary>
    /// Always <see cref="OpCode.CallIndirect"/>.
    /// </summary>
    public sealed override OpCode OpCode => OpCode.CallIndirect;

    /// <summary>
    /// The index of the type representing the function signature.
    /// </summary>
    public uint Type { get; set; }

    /// <summary>
    /// The table index to use for the indirect call.
    /// </summary>
    public uint Reserved { get; set; }

    /// <summary>
    /// The module byte offset where this instruction was parsed.
    /// </summary>
    internal long SourceOffset { get; set; } = -1;

    /// <summary>
    /// Creates a new  <see cref="CallIndirect"/> instance.
    /// </summary>
    public CallIndirect()
    {
    }

    /// <summary>
    /// Creates a new  <see cref="CallIndirect"/> instance.
    /// </summary>
    /// <param name="type">The index of the type representing the function signature.</param>
    public CallIndirect(uint type)
    {
        this.Type = type;
    }

    internal CallIndirect(Reader reader)
    {
        Type = reader.ReadVarUInt32();
        Reserved = reader.ReadVarUInt32();
    }

    internal sealed override void WriteTo(Writer writer)
    {
        writer.Write((byte)OpCode.CallIndirect);
        writer.WriteVar(this.Type);
        writer.WriteVar(this.Reserved);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => this.Equals(obj as CallIndirect);

    /// <summary>
    /// Determines whether this instruction is identical to another.
    /// </summary>
    /// <param name="other">The instruction to compare against.</param>
    /// <returns>True if they have the same type and value, otherwise false.</returns>
    public override bool Equals(Instruction? other) => this.Equals(other as CallIndirect);

    /// <summary>
    /// Determines whether this instruction is identical to another.
    /// </summary>
    /// <param name="other">The instruction to compare against.</param>
    /// <returns>True if they have the same type and value, otherwise false.</returns>
    public bool Equals(CallIndirect? other) =>
        other != null
        && other.Type == this.Type
        && other.Reserved == this.Reserved
        ;

    /// <summary>
    /// Returns a simple hash code based on the value of the instruction.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine((int)OpCode.CallIndirect, (int)this.Type, (int)this.Reserved);

    internal sealed override void Compile(CompilationContext context)
    {
        var signature = context.CheckedTypes[this.Type];
        var paramTypes = signature.RawParameterTypes;
        var returnTypes = signature.RawReturnTypes;
        var functionSignatures = context.CheckedFunctionSignatures;
        var methods = context.CheckedMethods;

        var stack = context.Stack;

        context.PopStackNoReturn(OpCode.CallIndirect, paramTypes.Cast<WebAssemblyValueType?>().Reverse().Prepend(WebAssemblyValueType.Int32), paramTypes.Length + 1);

        for (var i = 0; i < returnTypes.Length; i++)
            stack.Push(returnTypes[i]);

        // Get the table to use (Reserved field is table index in WASM 2.0)
        var tableIndex = this.Reserved;
        if (tableIndex >= (uint)context.Tables.Count)
            throw new ModuleLoadException($"call_indirect: table index {tableIndex} out of range.", 0);
        
        var table = context.GetTable(tableIndex);
        var tableElemType = context.GetTableElementType(tableIndex);
        
        if (tableElemType != ElementType.FunctionReference)
            throw new ModuleLoadException($"call_indirect: table {tableIndex} is not a funcref table.", 0);
        context.EmitLoadThis();
        var allDirectCallHints = context.Configuration.CallIndirectDirectCallHints?
            .Where(hint => hint.TypeIndex == signature.TypeIndex && hint.TableIndex == tableIndex)
            .OrderByDescending(hint => hint.Hotness)
            .ThenBy(hint => hint.ElementIndex)
            .ToArray()
            ?? [];
        var siteSpecificHints = allDirectCallHints
            .Where(hint => hint.SiteFunctionIndex == context.CurrentFunctionIndex && hint.SiteInstructionOffset == this.SourceOffset)
            .ToArray();
        var directCallHints = siteSpecificHints.Length != 0
            ? siteSpecificHints
            : allDirectCallHints.Where(hint => hint.SiteFunctionIndex == null && hint.SiteInstructionOffset == null).ToArray();
        var useSiteSpecificRemapper = siteSpecificHints.Length != 0;
        var remapperKey = useSiteSpecificRemapper
            ? (signature.TypeIndex, tableIndex, context.CurrentFunctionIndex, this.SourceOffset, true)
            : (signature.TypeIndex, tableIndex, 0u, -1L, false);
        if (!context.DelegateRemappersByType.TryGetValue(remapperKey, out var remapper))
        {
            var parms = signature.ParameterTypes;
            var returns = signature.ReturnTypes;

            foreach (var hint in directCallHints)
            {
                if (hint.FunctionIndex >= (uint)methods.Length)
                    throw new CompilerException($"call_indirect direct-call hint references unknown function index {hint.FunctionIndex}.");
                if (functionSignatures[hint.FunctionIndex].TypeIndex != signature.TypeIndex)
                    throw new CompilerException($"call_indirect direct-call hint for type {signature.TypeIndex} references incompatible function index {hint.FunctionIndex}.");
            }

            if (!context.DelegateInvokersByTypeIndex.TryGetValue(signature.TypeIndex, out var invoker))
            {
                var clrRetCount = returns.Length > 1 ? 1 : returns.Length;
                var del = context.Configuration.GetDelegateForType(parms.Length, clrRetCount) ??
                    throw new CompilerException($"Failed to get a delegate for type {signature}.");
                if (del.IsGenericType)
                    del = del.MakeGenericType(DelegateTypeArgs(parms, returns));
                context.DelegateInvokersByTypeIndex.Add(signature.TypeIndex, invoker = del.GetTypeInfo().GetDeclaredMethod(nameof(Action.Invoke))!);
            }

            context.DelegateRemappersByType.Add(remapperKey, remapper = context.CheckedExportsBuilder.DefineMethod(
                $"🔁 {signature.TypeIndex}@{tableIndex}",
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                MultiValueHelper.ClrReturnType(returns),
                [.. parms, typeof(uint), context.CheckedExportsBuilder]
                ));
            CompilationContext.SetHotPathImplementationFlags(remapper, inline: true);

            var il = remapper.GetILGenerator();
            var profilerField = context.CallIndirectProfiler;
            var needTargetLocal = profilerField != null || directCallHints.Length != 0;
            LocalBuilder? targetLocal = null;
            LocalBuilder? elementIndexLocal = null;
            if (needTargetLocal)
            {
                targetLocal = il.DeclareLocal(typeof(Delegate));
                elementIndexLocal = il.DeclareLocal(typeof(uint));
                il.EmitLoadArg(parms.Length);
                il.Emit(OpCodes.Stloc, elementIndexLocal);
            }
            il.EmitLoadArg(parms.Length + 1);
            il.Emit(OpCodes.Ldfld, table);
            il.Emit(OpCodes.Ldfld, FunctionTable.DelegatesField);
            if (elementIndexLocal != null)
                il.Emit(OpCodes.Ldloc, elementIndexLocal);
            else
                il.EmitLoadArg(parms.Length);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldelem_Ref);
            if (needTargetLocal)
                il.Emit(OpCodes.Stloc, targetLocal!);
            if (profilerField != null && targetLocal != null && elementIndexLocal != null)
            {
                var profilerLocal = il.DeclareLocal(typeof(ICallIndirectProfiler));
                var skipProfiler = il.DefineLabel();
                il.EmitLoadArg(parms.Length + 1);
                il.Emit(OpCodes.Ldfld, profilerField);
                il.Emit(OpCodes.Stloc, profilerLocal);
                il.Emit(OpCodes.Ldloc, profilerLocal);
                il.Emit(OpCodes.Brfalse_S, skipProfiler);
                il.Emit(OpCodes.Ldloc, profilerLocal);
                il.EmitLoadConstant(signature.TypeIndex);
                il.EmitLoadConstant(tableIndex);
                il.Emit(OpCodes.Ldloc, elementIndexLocal);
                il.Emit(OpCodes.Ldloc, targetLocal);
                il.EmitLoadConstant(context.CurrentFunctionIndex);
                il.EmitLoadConstant(checked((int)this.SourceOffset));
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Callvirt, typeof(ICallIndirectProfiler).GetMethod(nameof(ICallIndirectProfiler.Record))!);
                il.MarkLabel(skipProfiler);
            }
            if (directCallHints.Length != 0 && targetLocal != null && elementIndexLocal != null)
            {
                foreach (var hint in directCallHints)
                {
                    var nextHint = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, elementIndexLocal);
                    il.EmitLoadConstant(hint.ElementIndex);
                    il.Emit(OpCodes.Bne_Un_S, nextHint);
                    il.Emit(OpCodes.Ldloc, targetLocal);
                    il.EmitLoadArg(parms.Length + 1);
                    il.Emit(OpCodes.Ldfld, context.FunctionReferences!);
                    il.EmitLoadConstant((int)hint.FunctionIndex);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Bne_Un_S, nextHint);
                    for (var k = 0; k < parms.Length; k++)
                        il.EmitLoadArg(k);
                    il.EmitLoadArg(parms.Length + 1);
                    il.Emit(OpCodes.Call, methods[hint.FunctionIndex]);
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(nextHint);
                }
            }
            if (targetLocal != null)
                il.Emit(OpCodes.Ldloc, targetLocal);
            il.Emit(OpCodes.Castclass, invoker.DeclaringType!);
            for (var k = 0; k < parms.Length; k++)
                il.EmitLoadArg(k);

            il.Emit(OpCodes.Callvirt, invoker);
            il.Emit(OpCodes.Ret);
        }

        context.Emit(OpCodes.Call, remapper);

        if (returnTypes.Length > 1)
            EmitTupleUnpack(context, signature.ReturnTypes);
    }
}

using System.Reflection.Emit;
using WebAssembly.Runtime;
using WebAssembly.Runtime.Compilation;

namespace WebAssembly.Instructions;

/// <summary>
/// Set the current value of a local variable.
/// </summary>
public class LocalSet : VariableAccessInstruction
{
    /// <summary>
    /// Always <see cref="OpCode.LocalSet"/>.
    /// </summary>
    public sealed override OpCode OpCode => OpCode.LocalSet;

    /// <summary>
    /// Creates a new  <see cref="LocalSet"/> instance.
    /// </summary>
    public LocalSet()
    {
    }

    /// <summary>
    /// Creates a new <see cref="LocalSet"/> for the provided variable index.
    /// </summary>
    /// <param name="index">The index of the variable to access.</param>
    public LocalSet(uint index)
        : base(index)
    {
    }

    internal LocalSet(Reader reader)
        : base(reader)
    {
    }

    internal sealed override void Compile(CompilationContext context)
    {
        if (this.Index >= context.CheckedLocals.Length)
            throw new System.InvalidOperationException($"Attempt to set local at index {this.Index} but only {context.CheckedLocals.Length} {(context.CheckedLocals.Length == 1 ? "local was" : "locals were")} defined.");
        context.PopStackNoReturn(OpCode.LocalSet, context.CheckedLocals[this.Index]);

        context.EmitLocalSet(this.Index);
    }
}

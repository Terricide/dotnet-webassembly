using System.Reflection.Emit;
using WebAssembly.Runtime;
using WebAssembly.Runtime.Compilation;

namespace WebAssembly.Instructions;

/// <summary>
/// Read the current value of a local variable.
/// </summary>
public class LocalGet : VariableAccessInstruction
{
    /// <summary>
    /// Always <see cref="OpCode.LocalGet"/>.
    /// </summary>
    public sealed override OpCode OpCode => OpCode.LocalGet;

    /// <summary>
    /// Creates a new  <see cref="LocalGet"/> instance.
    /// </summary>
    public LocalGet()
    {
    }

    /// <summary>
    /// Creates a new <see cref="LocalGet"/> for the provided variable index.
    /// </summary>
    /// <param name="index">The index of the variable to access.</param>
    public LocalGet(uint index)
        : base(index)
    {
    }

    internal LocalGet(Reader reader)
        : base(reader)
    {
    }

    internal sealed override void Compile(CompilationContext context)
    {
        if (this.Index >= context.CheckedLocals.Length)
            throw new System.InvalidOperationException($"Attempt to get local at index {this.Index} but only {context.CheckedLocals.Length} {(context.CheckedLocals.Length == 1 ? "local was" : "locals were")} defined.");
        context.Stack.Push(context.CheckedLocals[this.Index]);

        context.EmitLocalGet(this.Index);
    }
}

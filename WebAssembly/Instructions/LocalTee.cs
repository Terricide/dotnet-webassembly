using System.Reflection.Emit;
using WebAssembly.Runtime;
using WebAssembly.Runtime.Compilation;

namespace WebAssembly.Instructions;

/// <summary>
/// Like <see cref="LocalSet"/>, but also returns the set value.
/// </summary>
public class LocalTee : VariableAccessInstruction
{
    /// <summary>
    /// Always <see cref="OpCode.LocalTee"/>.
    /// </summary>
    public sealed override OpCode OpCode => OpCode.LocalTee;

    /// <summary>
    /// Creates a new  <see cref="LocalTee"/> instance.
    /// </summary>
    public LocalTee()
    {
    }

    /// <summary>
    /// Creates a new <see cref="LocalTee"/> for the provided variable index.
    /// </summary>
    /// <param name="index">The index of the variable to access.</param>
    public LocalTee(uint index)
        : base(index)
    {
    }

    internal LocalTee(Reader reader)
        : base(reader)
    {
    }

    internal sealed override void Compile(CompilationContext context)
    {
        //Assuming validation passes, the remaining type will be context.CheckedLocals[this.Index]).
        context.ValidateStack(OpCode.LocalTee, context.CheckedLocals[this.Index]);

        context.EmitLocalTee(this.Index);
    }
}

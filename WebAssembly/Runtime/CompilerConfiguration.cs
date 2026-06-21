using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using WebAssembly.Runtime.Compilation;

namespace WebAssembly.Runtime;

/// <summary>
/// Configures the WebAssembly compiler.
/// </summary>
public class CompilerConfiguration
{
    /// <summary>
    /// Creates a new <see cref="CompilerConfiguration"/> instance with default properties.
    /// </summary>
    public CompilerConfiguration()
    {
    }

    internal virtual string CompiledTypeName => "CompiledExports";

    internal virtual Type NeutralizeType(Type type) => type;

    /// <summary>
    /// Gets or sets the dynamic assembly access mode used for runtime compilation.
    /// Defaults to <see cref="AssemblyBuilderAccess.RunAndCollect"/>.
    /// </summary>
    public AssemblyBuilderAccess DynamicAssemblyAccess { get; set; } = AssemblyBuilderAccess.RunAndCollect;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)] //Wrapped by a property
    private GetDelegateForTypeCallback getDelegateForType = GetStandardDelegateForType;

    /// <summary>
    /// A function that returns a generic delegate for the number of parameters and returns, used for imports.
    /// The default implementation is <see cref="GetStandardDelegateForType"/>.
    /// </summary>
    public GetDelegateForTypeCallback GetDelegateForType
    {
        get => getDelegateForType;
        set => getDelegateForType = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Creates an optional per-instance profiler for compiled <c>call_indirect</c> sites.
    /// When null, no profiling code is emitted.
    /// </summary>
    /// <remarks>This is supported for runtime compilation and intended as a foundation for future tiered/hot-path optimization work.</remarks>
    public Func<ICallIndirectProfiler?>? CreateCallIndirectProfiler { get; set; }

    /// <summary>
    /// Provides optional guarded direct-call candidates for compiled <c>call_indirect</c> remappers.
    /// Each hint is guarded by both element index and raw delegate identity, so normal fallback behavior is preserved when the table changes.
    /// </summary>
    public IReadOnlyList<CallIndirectDirectCallHint>? CallIndirectDirectCallHints { get; set; }

    /// <summary>
    /// When enabled, function bodies whose IL would exceed the .NET JIT's full-optimization size
    /// limit (which causes a fall back to unoptimized "MinOpts" codegen) are split: stack-isolated
    /// void <c>block</c>/<c>loop</c> regions that no branch escapes are lifted into separate helper
    /// methods small enough to be fully optimized. The locals such a region uses are passed by
    /// reference. Off by default.
    /// </summary>
    public bool EnableFunctionSplitting { get; set; }

    /// <summary>
    /// The approximate wasm-instruction count above which a function is considered oversized and
    /// eligible for splitting (only consulted when <see cref="EnableFunctionSplitting"/> is set).
    /// The JIT abandons full optimization near ~60 KB of IL, roughly 6,600 wasm instructions.
    /// </summary>
    public int FunctionSplitInstructionThreshold { get; set; } = 6000;

    /// <summary>
    /// The smallest region (in wasm instructions) worth lifting into a helper method when
    /// splitting; regions below this are left inline so a helper call never costs more than it
    /// saves. Only consulted when <see cref="EnableFunctionSplitting"/> is set.
    /// </summary>
    public int FunctionSplitMinimumRegionInstructions { get; set; } = 64;

    /// <summary>
    /// Returns the standard .NET delegate type, i.e. <see cref="Func{T, TResult}"/>/<see cref="Action"/> or their peers, for the provided parameter and return count.
    /// </summary>
    /// <param name="parameters">The number of parameters; if not 0 through 16 (inclusive), null is returned.</param>
    /// <param name="returns">The number of returns; if no 0 or 1, null is returned.</param>
    /// <returns>One of the <see cref="Func{T, TResult}"/>/<see cref="Action"/> variations,
    /// or null if no variation exists for the <paramref name="parameters"/>/<paramref name="returns"/> combination.</returns>
    /// <remarks>This can help build custom <see cref="GetDelegateForType"/> solutions by covering common cases.</remarks>
    public static Type? GetStandardDelegateForType(int parameters, int returns)
    {
        switch (returns)
        {
            case 0:
                switch (parameters)
                {
                    case 00: return typeof(Action);
                    case 01: return typeof(Action<>);
                    case 02: return typeof(Action<,>);
                    case 03: return typeof(Action<,,>);
                    case 04: return typeof(Action<,,,>);
                    case 05: return typeof(Action<,,,,>);
                    case 06: return typeof(Action<,,,,,>);
                    case 07: return typeof(Action<,,,,,,>);
                    case 08: return typeof(Action<,,,,,,,>);
                    case 09: return typeof(Action<,,,,,,,,>);
                    case 10: return typeof(Action<,,,,,,,,,>);
                    case 11: return typeof(Action<,,,,,,,,,,>);
                    case 12: return typeof(Action<,,,,,,,,,,,>);
                    case 13: return typeof(Action<,,,,,,,,,,,,>);
                    case 14: return typeof(Action<,,,,,,,,,,,,,>);
                    case 15: return typeof(Action<,,,,,,,,,,,,,,>);
                    case 16: return typeof(Action<,,,,,,,,,,,,,,,>);
                }
                break;
            case 1:
                switch (parameters)
                {
                    case 00: return typeof(Func<>);
                    case 01: return typeof(Func<,>);
                    case 02: return typeof(Func<,,>);
                    case 03: return typeof(Func<,,,>);
                    case 04: return typeof(Func<,,,,>);
                    case 05: return typeof(Func<,,,,,>);
                    case 06: return typeof(Func<,,,,,,>);
                    case 07: return typeof(Func<,,,,,,,>);
                    case 08: return typeof(Func<,,,,,,,,>);
                    case 09: return typeof(Func<,,,,,,,,,>);
                    case 10: return typeof(Func<,,,,,,,,,,>);
                    case 11: return typeof(Func<,,,,,,,,,,,>);
                    case 12: return typeof(Func<,,,,,,,,,,,,>);
                    case 13: return typeof(Func<,,,,,,,,,,,,,>);
                    case 14: return typeof(Func<,,,,,,,,,,,,,,>);
                    case 15: return typeof(Func<,,,,,,,,,,,,,,,>);
                    case 16: return typeof(Func<,,,,,,,,,,,,,,,,>);
                }
                break;
        }

        return null;
    }
}

/// <summary>
/// Provides a generic delegate type accepting the number of provided parameters and returns.
/// </summary>
/// <param name="parameters">The count of parameters.</param>
/// <param name="returns">The count of returns.</param>
/// <returns>
/// A generic delegate or null if one is not available--this will lead to a <see cref="MissingDelegateTypesException"/> with the list of all misses.
/// Typically, variants of <see cref="Func{T, TResult}"/>/<see cref="Action"/> are used, but these don't cover every possibility.
/// If more than 16 parameters are needed, a custom delegate type must be created.</returns>
/// <remarks><see cref="CompilerConfiguration.GetStandardDelegateForType(int, int)"/> can be combined with custom solutions to handle common cases.</remarks>
public delegate Type? GetDelegateForTypeCallback(int parameters, int returns);

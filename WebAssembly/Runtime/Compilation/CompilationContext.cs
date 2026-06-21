using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using WebAssembly.Instructions;
using ILOpCode = System.Reflection.Emit.OpCode;

namespace WebAssembly.Runtime.Compilation;

internal sealed class CompilationContext(CompilerConfiguration configuration)
{
    private TypeBuilder? ExportsBuilder;
    private ILGenerator? generator;
    public readonly CompilerConfiguration Configuration = configuration;

    sealed class FunctionOuterBlock : BlockTypeInstruction
    {
        public override OpCode OpCode => OpCode.Return; // "Return" is the most accurate fake opcode for the outer block.

        public FunctionOuterBlock(BlockType type) : base(type) { }

        internal override void Compile(CompilationContext context) => throw new NotSupportedException();
    }

    public void Reset(
        ILGenerator generator,
        Signature signature,
        WebAssemblyValueType[] locals
        )
    {
        this.generator = generator;
        this.Signature = signature;
        this.Locals = locals;

        this.Depth.Clear();
        {
            BlockType returnType;
            if (signature.RawReturnTypes.Length == 0)
            {
                returnType = BlockType.Empty;
            }
            else if (signature.RawReturnTypes.Length == 1)
            {
                returnType = signature.RawReturnTypes[0] switch
                {
                    WebAssemblyValueType.Int64 => BlockType.Int64,
                    WebAssemblyValueType.Float32 => BlockType.Float32,
                    WebAssemblyValueType.Float64 => BlockType.Float64,
                    WebAssemblyValueType.V128 => BlockType.V128,
                    _ => BlockType.Int32,
                };
            }
            else
            {
                // Multi-value: use Empty as placeholder; Return/End read RawReturnTypes directly.
                returnType = BlockType.Empty;
            }
            this.Depth.Push(new FunctionOuterBlock(returnType));
        }
        this.Previous = OpCode.NoOperation;
        this.Labels.Clear();
        this.LoopLabels.Clear();
        this.Stack.Clear();
        this.BlockContexts.Clear();
        this.BlockContexts.Add(this.Depth.Count, new BlockContext());

        this.Labels.Add(0, generator.DefineLabel());

        this.MemoryStartLocal = null;
        this.MemorySizeLocal = null;
        this.scratchLocals.Clear();
    }

    /// <summary>
    /// Caches <see cref="UnmanagedMemory.RawStart"/> for the current function so memory accesses
    /// avoid repeated field loads. Null when the current function doesn't access linear memory.
    /// </summary>
    public LocalBuilder? MemoryStartLocal;

    /// <summary>
    /// Caches <see cref="UnmanagedMemory.RawSize"/> (zero-extended to 64 bits) for the current function.
    /// </summary>
    public LocalBuilder? MemorySizeLocal;

    public bool MemoryCacheActive => this.MemoryStartLocal is not null;

    private readonly Dictionary<(Type Type, string Purpose), LocalBuilder> scratchLocals = [];

    /// <summary>
    /// Returns a function-scoped scratch local shared by all instructions that request the same
    /// type and purpose. Only safe for values whose lifetime is contained within the IL emitted
    /// for a single instruction, with no other scratch user of the same key in between.
    /// </summary>
    public LocalBuilder GetScratchLocal(Type type, string purpose)
    {
        if (!this.scratchLocals.TryGetValue((type, purpose), out var local))
            this.scratchLocals.Add((type, purpose), local = this.DeclareLocal(type));
        return local;
    }

    /// <summary>
    /// Declares and initializes the memory base/size cache locals. Must be emitted in the
    /// function prologue, before any instruction that may consume them.
    /// </summary>
    public void EmitInitMemoryCache()
    {
        this.MemoryStartLocal = this.DeclareLocal(typeof(IntPtr));
        this.MemorySizeLocal = this.DeclareLocal(typeof(long));
        this.EmitRefreshMemoryCache();
    }

    /// <summary>
    /// Re-reads the memory base/size into the cache locals. Must be emitted after every operation
    /// that may grow linear memory (calls, indirect calls, memory.grow) because growth can both
    /// change the size and relocate the buffer. Stack-neutral.
    /// </summary>
    public void EmitRefreshMemoryCache()
    {
        if (this.MemoryStartLocal is null || this.MemorySizeLocal is null)
            return;

        this.EmitLoadThis();
        this.Emit(OpCodes.Ldfld, this.CheckedMemory);
        this.Emit(OpCodes.Dup);
        this.Emit(OpCodes.Ldfld, UnmanagedMemory.StartField);
        this.Emit(OpCodes.Stloc, this.MemoryStartLocal);
        this.Emit(OpCodes.Ldfld, UnmanagedMemory.SizeField);
        this.Emit(OpCodes.Conv_U8);
        this.Emit(OpCodes.Stloc, this.MemorySizeLocal);
    }

    public Signature[]? FunctionSignatures;

    public MethodInfo[]? Methods;

    public Signature[]? Types;

    public GlobalInfo[]? Globals;

    /// <summary>Number of globals that came from the import section (indices 0..ImportedGlobalCount-1).</summary>
    public int ImportedGlobalCount;

    /// <summary>Element types for each table, indexed by table index (funcref or externref).</summary>
    public readonly List<ElementType> TableElementTypes = [];

    public readonly Dictionary<uint, MethodInfo> DelegateInvokersByTypeIndex = [];

    public readonly Dictionary<(uint TypeIndex, uint TableIndex, uint FunctionIndex, long InstructionOffset, bool IsSiteSpecific), MethodBuilder> DelegateRemappersByType = [];

    /// <summary>
    /// Function indices that are valid ref.func targets for function bodies.
    /// Populated from function exports and element segments as the module is read.
    /// </summary>
    public readonly HashSet<uint> DeclaredFunctionReferences = [];

    /// <summary>
    /// Table fields, indexed by table index. Each field is either a FunctionTable or ExternRefTable.
    /// For backward compatibility, Tables[0] is also accessible via FunctionTable property.
    /// </summary>
    public readonly List<FieldBuilder> Tables = [];

    /// <summary>
    /// Legacy property for backward compatibility - returns the first table (index 0) if it exists.
    /// For new code, use Tables[index] directly.
    /// </summary>
    public FieldBuilder? FunctionTable => Tables.Count > 0 ? Tables[0] : null;

    /// <summary>
    /// Array field holding function references (delegates) for ref.func instruction.
    /// Initialized during module compilation with delegate instances for each function.
    /// May be null if the module doesn't use ref.func.
    /// </summary>
#pragma warning disable CS0649 // Field is never assigned to
    public FieldBuilder? FunctionReferences;
#pragma warning restore CS0649

    /// <summary>Maps data segment index → FieldBuilder for passive segment byte[] fields.</summary>
    public readonly Dictionary<uint, FieldBuilder> DataSegments = [];

    /// <summary>Maps element segment index → FieldBuilder for element-segment backing fields.</summary>
    public readonly Dictionary<uint, FieldBuilder> ElementSegments = [];

    /// <summary>Maps element segment index → its element type (funcref or externref).</summary>
    public readonly Dictionary<uint, ElementType> ElementSegmentTypes = [];

    /// <summary>Indices of passive element segments, which are the only segments valid for table.init and elem.drop.</summary>
    public readonly HashSet<uint> PassiveElementSegments = [];

    /// <summary>
    /// Indicates whether ref.func declaration checks should be enforced during compilation.
    /// This is enabled for function bodies after exports/element segments have been read.
    /// </summary>
    public bool EnforceDeclaredFunctionReferences;

    internal const MethodAttributes HelperMethodAttributes =
        MethodAttributes.Private |
        MethodAttributes.Static |
        MethodAttributes.HideBySig
        ;

    internal static void SetHotPathImplementationFlags(MethodBuilder builder, bool inline = false)
    {
        var flags = default(MethodImplAttributes);
        if (inline)
            flags |= MethodImplAttributes.AggressiveInlining;
#if NET8_0_OR_GREATER
        flags |= MethodImplAttributes.AggressiveOptimization;
#endif
        if (flags != default)
            builder.SetImplementationFlags(flags);
    }

    private readonly Dictionary<HelperMethod, MethodBuilder> helperMethods = [];
    private readonly Dictionary<string, FieldBuilder> v128ConstantFields = [];

    public MethodInfo this[HelperMethod helper]
    {
        get
        {
            if (this.helperMethods.TryGetValue(helper, out var builder))
                return builder;

            throw new InvalidOperationException(); // Shouldn't be possible.
        }
    }

    public MethodInfo this[HelperMethod helper, Func<HelperMethod, CompilationContext, MethodBuilder> creator]
    {
        get
        {
            if (this.helperMethods.TryGetValue(helper, out var builder))
                return builder;

            this.helperMethods.Add(helper, builder = creator(helper, this));
            return builder;
        }
    }

    public FieldBuilder GetOrCreateV128ConstantField(byte[] value, string namePrefix)
    {
        var key = BitConverter.ToString(value);
        if (this.v128ConstantFields.TryGetValue(key, out var field))
            return field;

        field = this.CheckedExportsBuilder.DefineInitializedData(
            $"{namePrefix} {this.v128ConstantFields.Count}",
            value,
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
        this.v128ConstantFields.Add(key, field);
        return field;
    }

    public Signature? Signature;

    public FieldBuilder? Memory;

    public FieldBuilder? CallIndirectProfiler;

    public WebAssemblyValueType MemoryAddressType = WebAssemblyValueType.Int32;

    public WebAssemblyValueType[]? Locals;

    /// <summary>
    /// The <see cref="LocalBuilder"/>s for the current function's wasm locals (those beyond the
    /// parameters), indexed by local index (wasm index minus parameter count). Used by the
    /// outlined-region placeholder to pass locals to outlined helpers by reference.
    /// </summary>
    public LocalBuilder[] WasmLocalBuilders = [];

    public readonly BlockStack Depth = new();

    public OpCode Previous;

    public uint CurrentFunctionIndex;

    public readonly Dictionary<uint, Label> Labels = [];

    public readonly HashSet<Label> LoopLabels = [];

    public readonly Stack<WebAssemblyValueType> Stack = new();

    public readonly Dictionary<int, BlockContext> BlockContexts = [];

    public WebAssemblyValueType[] CheckedLocals => Locals ?? throw new InvalidOperationException();

    public Signature[] CheckedFunctionSignatures => FunctionSignatures ?? throw new InvalidOperationException();

    public MethodInfo[] CheckedMethods => Methods ?? throw new InvalidOperationException();

    public Signature[] CheckedTypes => Types ?? throw new InvalidOperationException();

    public FieldBuilder CheckedMemory => Memory ?? throw new InvalidOperationException();

    public TypeBuilder CheckedExportsBuilder
    {
        get => this.ExportsBuilder ?? throw new InvalidOperationException();
        set
        {
            this.ExportsBuilder = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    private ILGenerator CheckedGenerator => this.generator ?? throw new InvalidOperationException();

    public Signature CheckedSignature => this.Signature ?? throw new InvalidOperationException();

    public Label DefineLabel() => CheckedGenerator.DefineLabel();

    public void MarkLabel(Label loc) => CheckedGenerator.MarkLabel(loc);

    /// <summary>
    /// When non-null, the current method is an outlined helper: wasm locals are accessed through
    /// by-reference parameters rather than this frame's args/locals. See <see cref="OutliningEnvironment"/>.
    /// </summary>
#pragma warning disable CS0649 // Assigned by the function-splitting driver in Compile.cs.
    public OutliningEnvironment? Outlining;
#pragma warning restore CS0649

    /// <summary>
    /// Describes how an outlined helper method reaches the original function's locals and instance.
    /// </summary>
    public sealed class OutliningEnvironment(IReadOnlyDictionary<uint, int> localToArgument, int thisArgument)
    {
        /// <summary>Maps a wasm local/param index to the helper argument (a managed by-ref) that aliases it.</summary>
        public IReadOnlyDictionary<uint, int> LocalToArgument { get; } = localToArgument;

        /// <summary>The helper argument index holding the <c>CompiledExports</c> instance.</summary>
        public int ThisArgument { get; } = thisArgument;
    }

    public void EmitLoadThis()
    {
        if (this.Outlining is { } env)
            CheckedGenerator.EmitLoadArg(env.ThisArgument);
        else
            CheckedGenerator.EmitLoadArg(CheckedSignature.ParameterTypes.Length);
    }

    /// <summary>Emits a read of the wasm local/param at <paramref name="index"/> onto the stack.</summary>
    public void EmitLocalGet(uint index)
    {
        if (this.Outlining is { } env && env.LocalToArgument.TryGetValue(index, out var arg))
        {
            CheckedGenerator.EmitLoadArg(arg); // managed by-ref to the original local
            EmitLoadIndirect(this.CheckedLocals[index]);
            return;
        }

        var localIndex = index - CheckedSignature.ParameterTypes.Length;
        if (localIndex < 0)
        {
            switch (index)
            {
                case 0: Emit(OpCodes.Ldarg_0); break;
                case 1: Emit(OpCodes.Ldarg_1); break;
                case 2: Emit(OpCodes.Ldarg_2); break;
                case 3: Emit(OpCodes.Ldarg_3); break;
                default:
                    if (index <= byte.MaxValue)
                        Emit(OpCodes.Ldarg_S, checked((byte)index));
                    else
                        Emit(OpCodes.Ldarg, checked((int)(ushort)index));
                    break;
            }
        }
        else
        {
            switch (localIndex)
            {
                case 0: Emit(OpCodes.Ldloc_0); break;
                case 1: Emit(OpCodes.Ldloc_1); break;
                case 2: Emit(OpCodes.Ldloc_2); break;
                case 3: Emit(OpCodes.Ldloc_3); break;
                default:
                    if (localIndex > 65534)
                        throw new CompilerException($"Implementation limit exceeded: maximum accessible local index is 65534, tried to access {localIndex}.");
                    if (localIndex <= byte.MaxValue)
                        Emit(OpCodes.Ldloc_S, (byte)localIndex);
                    else
                        Emit(OpCodes.Ldloc, checked((int)(ushort)localIndex));
                    break;
            }
        }
    }

    /// <summary>Emits a write of the top stack value into the wasm local/param at <paramref name="index"/>.</summary>
    public void EmitLocalSet(uint index)
    {
        if (this.Outlining is { } env && env.LocalToArgument.TryGetValue(index, out var arg))
        {
            var type = this.CheckedLocals[index];
            var tmp = GetScratchLocal(type.ToSystemType(), "outlineLocalSet");
            Emit(OpCodes.Stloc, tmp);     // stash value
            CheckedGenerator.EmitLoadArg(arg); // by-ref destination
            Emit(OpCodes.Ldloc, tmp);     // value
            EmitStoreIndirect(type);
            return;
        }

        var localIndex = index - CheckedSignature.ParameterTypes.Length;
        if (localIndex < 0)
        {
            if (index <= byte.MaxValue)
                Emit(OpCodes.Starg_S, checked((byte)index));
            else
                Emit(OpCodes.Starg, checked((int)(ushort)index));
        }
        else
        {
            switch (localIndex)
            {
                case 0: Emit(OpCodes.Stloc_0); break;
                case 1: Emit(OpCodes.Stloc_1); break;
                case 2: Emit(OpCodes.Stloc_2); break;
                case 3: Emit(OpCodes.Stloc_3); break;
                default:
                    if (localIndex > 65534)
                        throw new CompilerException($"Implementation limit exceeded: maximum accessible local index is 65534, tried to access {localIndex}.");
                    if (localIndex <= byte.MaxValue)
                        Emit(OpCodes.Stloc_S, (byte)localIndex);
                    else
                        Emit(OpCodes.Stloc, checked((int)(ushort)localIndex));
                    break;
            }
        }
    }

    /// <summary>Like <see cref="EmitLocalSet"/> but leaves the value on the stack (local.tee).</summary>
    public void EmitLocalTee(uint index)
    {
        Emit(OpCodes.Dup);
        EmitLocalSet(index);
    }

    private void EmitLoadIndirect(WebAssemblyValueType type)
    {
        switch (type)
        {
            case WebAssemblyValueType.Int32: Emit(OpCodes.Ldind_I4); break;
            case WebAssemblyValueType.Int64: Emit(OpCodes.Ldind_I8); break;
            case WebAssemblyValueType.Float32: Emit(OpCodes.Ldind_R4); break;
            case WebAssemblyValueType.Float64: Emit(OpCodes.Ldind_R8); break;
            case WebAssemblyValueType.V128: Emit(OpCodes.Ldobj, V128Helper.V128Type); break;
            default: Emit(OpCodes.Ldind_Ref); break; // funcref / externref
        }
    }

    private void EmitStoreIndirect(WebAssemblyValueType type)
    {
        switch (type)
        {
            case WebAssemblyValueType.Int32: Emit(OpCodes.Stind_I4); break;
            case WebAssemblyValueType.Int64: Emit(OpCodes.Stind_I8); break;
            case WebAssemblyValueType.Float32: Emit(OpCodes.Stind_R4); break;
            case WebAssemblyValueType.Float64: Emit(OpCodes.Stind_R8); break;
            case WebAssemblyValueType.V128: Emit(OpCodes.Stobj, V128Helper.V128Type); break;
            default: Emit(OpCodes.Stind_Ref); break; // funcref / externref
        }
    }

    public void Emit(ILOpCode opcode) => CheckedGenerator.Emit(opcode);

    public void Emit(ILOpCode opcode, byte arg) => CheckedGenerator.Emit(opcode, arg);

    public void Emit(ILOpCode opcode, int arg) => CheckedGenerator.Emit(opcode, arg);

    public void Emit(ILOpCode opcode, long arg) => CheckedGenerator.Emit(opcode, arg);

    public void Emit(ILOpCode opcode, float arg) => CheckedGenerator.Emit(opcode, arg);

    public void Emit(ILOpCode opcode, double arg) => CheckedGenerator.Emit(opcode, arg);

    public void Emit(ILOpCode opcode, Label label) => CheckedGenerator.Emit(opcode, label);

    public void Emit(ILOpCode opcode, Label[] labels) => CheckedGenerator.Emit(opcode, labels);

    public void Emit(ILOpCode opcode, FieldInfo field) => CheckedGenerator.Emit(opcode, field);

    public void Emit(ILOpCode opcode, MethodInfo meth) => CheckedGenerator.Emit(opcode, meth);

    public void Emit(ILOpCode opcode, ConstructorInfo con) => CheckedGenerator.Emit(opcode, con);

    public void Emit(ILOpCode opcode, Type type) => CheckedGenerator.Emit(opcode, type);

    public LocalBuilder DeclareLocal(Type localType) => CheckedGenerator.DeclareLocal(localType);

    public void Emit(ILOpCode opcode, LocalBuilder local) => CheckedGenerator.Emit(opcode, local);

    public WebAssemblyValueType? PopStack(OpCode opcode, WebAssemblyValueType? expectedType)
    {
        return PopStack(opcode, [expectedType], 1).FirstOrDefault();
    }

    public void PopStackNoReturn(OpCode opcode)
    {
        var stackCount = this.Stack.Count;

        if (stackCount <= this.CurrentBlockContext.InitialStackSize)
        {
            if (this.IsUnreachable)
                return;

            throw new StackTooSmallException(opcode, 1, stackCount);
        }

        this.Stack.Pop();
    }

    public void PopStackNoReturn(OpCode opcode, WebAssemblyValueType expectedType)
    {
        var stackCount = this.Stack.Count;

        if (stackCount <= this.CurrentBlockContext.InitialStackSize)
        {
            if (this.IsUnreachable)
                return;

            throw new StackTooSmallException(opcode, 1, stackCount);
        }

        var type = this.Stack.Pop();
        if (type != expectedType)
            throw new StackTypeInvalidException(opcode, expectedType, type);
    }

    public void PopStackNoReturn(OpCode opcode, WebAssemblyValueType expectedType1, WebAssemblyValueType expectedType2)
    {
        var initialStackSize = this.Stack.Count;
        var blockContextInitialStackSize = this.CurrentBlockContext.InitialStackSize;

        var expected = expectedType1;
        if (initialStackSize <= blockContextInitialStackSize)
        {
            if (this.IsUnreachable)
                return;

            throw new StackTooSmallException(opcode, 2, initialStackSize);
        }

        var type = this.Stack.Pop();
        if (type != expected)
            throw new StackTypeInvalidException(opcode, expected, type);

        expected = expectedType2;
        if (initialStackSize - 1 <= blockContextInitialStackSize)
        {
            if (this.IsUnreachable)
                return;

            throw new StackTooSmallException(opcode, 2, initialStackSize);
        }

        type = this.Stack.Pop();
        if (type != expected)
            throw new StackTypeInvalidException(opcode, expected, type);
    }

    public void PopStackNoReturn(OpCode opcode, IEnumerable<WebAssemblyValueType?> expectedTypes, int expectedCount)
    {
        var initialStackSize = this.Stack.Count;
        var blockContextInitialStackSize = this.CurrentBlockContext.InitialStackSize;

        foreach (var expected in expectedTypes)
        {
            if (this.Stack.Count <= blockContextInitialStackSize)
            {
                if (this.IsUnreachable)
                    continue;

                throw new StackTooSmallException(opcode, expectedCount, initialStackSize);
            }

            var type = this.Stack.Pop();
            if (expected.HasValue && type != expected)
                throw new StackTypeInvalidException(opcode, expected.Value, type);
        }
    }

    /// <summary>
    /// Pop multiple types from stack and test whether they match with expected types.
    /// The algorithm is based on the validation algorithm described in WASM spec.
    /// See: https://webassembly.github.io/spec/core/appendix/algorithm.html
    /// </summary>
    /// <param name="opcode">OpCode of the instruction (for exception message).</param>
    /// <param name="expectedTypes">Sequence of expected types (or null, which indicates any type is accepted)</param>
    /// <param name="expectedCount">The number of expected types.</param>
    /// <returns>Sequence of actually popped types (or null, which indicates unknown type).</returns>
    public IEnumerable<WebAssemblyValueType?> PopStack(OpCode opcode, IEnumerable<WebAssemblyValueType?> expectedTypes, int expectedCount)
    {
        var actualTypes = new List<WebAssemblyValueType?>(expectedCount);
        var initialStackSize = this.Stack.Count;
        var blockContextInitialStackSize = this.CurrentBlockContext.InitialStackSize;

        foreach (var expected in expectedTypes)
        {
            WebAssemblyValueType? type;

            if (this.Stack.Count <= blockContextInitialStackSize)
            {
                if (this.IsUnreachable)
                    type = null;
                else
                    throw new StackTooSmallException(opcode, expectedCount, initialStackSize);
            }
            else
            {
                type = this.Stack.Pop();
            }

            if (type.HasValue)
            {
                if (expected.HasValue && type != expected)
                    throw new StackTypeInvalidException(opcode, expected.Value, type.Value);
            }
            else
            {
                type = expected;
            }

            actualTypes.Add(type);
        }

        return actualTypes;
    }

    public void ValidateStack(OpCode opcode, WebAssemblyValueType expectedType)
    {
        this.PopStackNoReturn(opcode, expectedType);
        this.Stack.Push(expectedType);
    }

    private BlockContext CurrentBlockContext => this.BlockContexts[this.Depth.Count];

    /// <summary>
    /// Returns the result-carrying local for the block at the given depth (ancestor index from current).
    /// The local is allocated on first access.
    /// </summary>
    public LocalBuilder GetOrCreateResultLocal(int depthIndex, WebAssemblyValueType valueType)
    {
        var targetDepthKey = this.Depth.Count - depthIndex;
        var blockCtx = this.BlockContexts[targetDepthKey];
        if (blockCtx.ResultLocal == null)
            blockCtx.ResultLocal = this.DeclareLocal(valueType.ToSystemType());
        return blockCtx.ResultLocal;
    }


    /// <summary>
    /// Marks the subsequent instructions as unreachable.
    /// </summary>
    public void MarkUnreachable(bool functionWide = false)
    {
        var blockContext = this.CurrentBlockContext;
        blockContext.MarkUnreachable();

        if (functionWide)
        {
            for (var i = this.Depth.Count; i > 1; i--)
            {
                this.BlockContexts[i].MarkUnreachable();
            }
        }

        //Revert the stack state into beginning of the current block
        //This is based on the validation algorithm defined in WASM spec.
        //See: https://webassembly.github.io/spec/core/appendix/algorithm.html
        while (this.Stack.Count > blockContext.InitialStackSize)
        {
            this.Stack.Pop();
        }
    }

    public void MarkReachable()
    {
        this.CurrentBlockContext.MarkReachable();
    }

    public bool IsUnreachable => this.CurrentBlockContext.IsUnreachable;

    /// <summary>
    /// Gets the table field for the specified table index.
    /// </summary>
    public FieldBuilder GetTable(uint tableIndex)
    {
        if (tableIndex >= (uint)Tables.Count)
            throw new InvalidOperationException($"Table index {tableIndex} out of range (only {Tables.Count} tables defined)");
        return Tables[(int)tableIndex];
    }

    /// <summary>
    /// Gets the element type for the specified table index.
    /// </summary>
    public ElementType GetTableElementType(uint tableIndex)
    {
        if (tableIndex >= (uint)TableElementTypes.Count)
            throw new InvalidOperationException($"Table index {tableIndex} out of range (only {TableElementTypes.Count} table types defined)");
        return TableElementTypes[(int)tableIndex];
    }

}

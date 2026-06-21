using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WebAssembly.Instructions;

namespace WebAssembly.Runtime;

/// <summary>
/// Tests <see cref="CompilerConfiguration.EnableFunctionSplitting"/>: oversized function bodies are
/// rewritten so stack-isolated void block/loop regions become separate helper methods, with the
/// locals they use passed by reference. The behavior must be identical to the un-split compilation.
/// </summary>
[TestClass]
public class FunctionSplittingTests
{
    /// <summary>
    /// Builds a function (param i32 → result i32, locals i32 and i64) whose body is two large void
    /// blocks plus a small tail. The blocks exercise i32 and i64 locals (by-ref ld/st of both
    /// widths), a linear-memory round trip (so a helper initializes its own memory cache), and a
    /// nested block with an internal <c>br_if</c> (a branch that stays inside its region).
    /// </summary>
    private static List<Instruction> BuildBody()
    {
        var code = new List<Instruction>
        {
            // ── region A ──────────────────────────────────────────────────────────
            new Block(BlockType.Empty),
        };

        for (var i = 0; i < 12; i++)
        {
            code.Add(new LocalGet(1));          // i32 local
            code.Add(new LocalGet(0));          // param
            code.Add(new Int32Constant(i + 1));
            code.Add(new Int32Add());
            code.Add(new Int32Add());
            code.Add(new LocalSet(1));
        }

        // Mix the i32 local into the i64 local (exercises ldind.i8 / stind.i8 by ref).
        code.Add(new LocalGet(2));
        code.Add(new LocalGet(1));
        code.Add(new Int64ExtendInt32Signed());
        code.Add(new Int64Add());
        code.Add(new LocalSet(2));

        // Memory round trip so the outlined helper sets up and uses its own memory cache.
        code.Add(new Int32Constant(0));
        code.Add(new LocalGet(1));
        code.Add(new Int32Store());
        code.Add(new Int32Constant(0));
        code.Add(new Int32Load());
        code.Add(new LocalSet(1));

        code.Add(new End()); // end region A

        // ── region B: nested block with an internal br_if ─────────────────────────
        code.Add(new Block(BlockType.Empty)); // region B
        code.Add(new Block(BlockType.Empty)); // inner block (br_if target)
        for (var i = 0; i < 12; i++)
        {
            code.Add(new LocalGet(1));
            code.Add(new Int32Constant(0));
            code.Add(new Int32Equal());
            code.Add(new BranchIf(0)); // branch to the inner block — stays inside region B
            code.Add(new LocalGet(1));
            code.Add(new Int32Constant(1));
            code.Add(new Int32Subtract());
            code.Add(new LocalSet(1));
        }
        code.Add(new End()); // end inner block
        code.Add(new End()); // end region B

        // ── tail: result = local1 + (i32)local2 ───────────────────────────────────
        code.Add(new LocalGet(1));
        code.Add(new LocalGet(2));
        code.Add(new Int32WrapInt64());
        code.Add(new Int32Add());
        code.Add(new End()); // end function

        return code;
    }

    private static Module BuildModule()
    {
        var module = new Module();
        module.Types.Add(new WebAssemblyType
        {
            Parameters = [WebAssemblyValueType.Int32],
            Returns = [WebAssemblyValueType.Int32],
        });
        module.Functions.Add(new Function());
        module.Memories.Add(new Memory(1, 1));
        module.Exports.Add(new Export("Test"));
        module.Codes.Add(new FunctionBody
        {
            Locals =
            [
                new Local { Count = 1, Type = WebAssemblyValueType.Int32 },
                new Local { Count = 1, Type = WebAssemblyValueType.Int64 },
            ],
            Code = BuildBody(),
        });
        return module;
    }

    private static CompilerTestBase<int> Compile(Module module, CompilerConfiguration? configuration)
    {
        using var memory = new MemoryStream();
        module.WriteToBinary(memory);
        memory.Position = 0;
        var maker = configuration is null
            ? Runtime.Compile.FromBinary<CompilerTestBase<int>>(memory)
            : Runtime.Compile.FromBinary<CompilerTestBase<int>>(memory, configuration);
        return maker(new ImportDictionary()).Exports;
    }

    /// <summary>
    /// A split compilation must produce exactly the same results as an un-split one across inputs.
    /// </summary>
    [TestMethod]
    public void FunctionSplitting_MatchesUnsplitResults()
    {
        var module = BuildModule();
        var bodyLength = module.Codes[0].Code.Count;

        var baseline = Compile(module, null);

        var splitConfiguration = new CompilerConfiguration
        {
            EnableFunctionSplitting = true,
            // Force this modestly-sized body over the threshold and allow small helper regions.
            FunctionSplitInstructionThreshold = bodyLength - 1,
            FunctionSplitMinimumRegionInstructions = 8,
        };
        var split = Compile(module, splitConfiguration);

        for (var input = -5; input <= 50; input++)
            Assert.AreEqual(baseline.Test(input), split.Test(input), $"Mismatch at input {input}.");
    }

    /// <summary>
    /// Confirms splitting actually occurred (helper methods were generated), so the equality test
    /// above is meaningfully exercising the outlined path rather than silently falling back.
    /// </summary>
    [TestMethod]
    public void FunctionSplitting_ProducesHelperMethods()
    {
        var module = BuildModule();
        var bodyLength = module.Codes[0].Code.Count;

        var split = Compile(module, new CompilerConfiguration
        {
            EnableFunctionSplitting = true,
            FunctionSplitInstructionThreshold = bodyLength - 1,
            FunctionSplitMinimumRegionInstructions = 8,
        });

        var helperMethods = split.GetType()
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .Count(method => method.Name.Contains("🪓", StringComparison.Ordinal));

        Assert.IsTrue(helperMethods >= 2, $"Expected outlined helper methods, found {helperMethods}.");
    }

    /// <summary>
    /// When splitting is disabled (the default) no helper methods are produced.
    /// </summary>
    [TestMethod]
    public void FunctionSplitting_DisabledByDefault()
    {
        var module = BuildModule();
        var baseline = Compile(module, null);

        var helperMethods = baseline.GetType()
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .Count(method => method.Name.Contains("🪓", StringComparison.Ordinal));

        Assert.AreEqual(0, helperMethods);
    }
}

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WebAssembly.Instructions;

/// <summary>
/// Tests the <see cref="ElemDrop"/> instruction.
/// </summary>
[TestClass]
public class ElemDropTests
{
    /// <summary>Export with no return.</summary>
    public abstract class VoidExport
    {
        /// <summary>Runs the function.</summary>
        public abstract void Test();
    }

    /// <summary>
    /// Tests that elem.drop on an existing non-passive segment compiles without error and runs as a no-op.
    /// </summary>
    [TestMethod]
    public void ElemDrop_NonPassiveSegmentIsNoOp()
    {
        var module = new Module();
        module.Tables.Add(new Table(1, null));
        module.Types.Add(new WebAssemblyType { Returns = [], Parameters = [] });
        module.Functions.Add(new Function());
        module.Exports.Add(new Export { Name = nameof(VoidExport.Test) });
        module.Codes.Add(new FunctionBody
        {
            Code =
            [
                new ElemDrop { SegmentIndex = 0 },
                new End(),
            ],
        });
        module.Elements.Add(new Element { Kind = 3, Elements = [0u] });

        // elem.drop on an existing non-passive segment (here: declarative) is a no-op.
        var instance = module.ToInstance<VoidExport>();
        instance.Exports.Test(); // Should not throw.
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using TextToAnimation.EditorTools.Inference.Onnx;
using TextToAnimation.EditorTools.Inference.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

public class OnnxRuntimeTests
{
    readonly ITestOutputHelper _out;
    public OnnxRuntimeTests(ITestOutputHelper output) => _out = output;

    static Tensor Npy(string name)
    {
        var a = NumpyArray.ReadFile(Fixtures.Path("onnx/" + name));
        return Tensor.Float(a.Shape, a.Values);
    }

    [Fact]
    public void TransformerBlockMatchesOnnxRuntime()
    {
        var session = OnnxSession.Load(Fixtures.Path("onnx/block.onnx"));
        var mask = Npy("block_mask.npy");
        var outputs = session.Run(new Dictionary<string, Tensor>
        {
            ["x"] = Npy("block_x.npy"),
            ["cond"] = Npy("block_cond.npy"),
            ["mask"] = Tensor.Bool(mask.Shape, mask.F.Select(v => v != 0).ToArray()),
        });
        var y = outputs["y"];
        var expected = Npy("block_y.npy");
        Assert.Equal(expected.Shape, y.Shape);
        var err = y.F.Zip(expected.F, (a, b) => MathF.Abs(a - b)).Max();
        _out.WriteLine($"max abs error {err}");
        Assert.True(err < 1e-4f, $"max abs error {err}");
    }

    [Fact]
    public void BuilderWrittenGraphRoundTrips()
    {
        var b = new OnnxGraphBuilder(null);
        b.Input("x", OnnxGraphBuilder.Float, 2, 3);
        var w = b.Weight("w", OnnxGraphBuilder.Float, new long[] { 3, 2 }, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new float[] { 1, 2, 3, 4, 5, 6 }.AsSpan()));
        var mm = b.Node("MatMul", new[] { "x", w });
        var sm = b.Node("Softmax", new[] { mm }, a => a.Int("axis", -1));
        b.Identity(sm, "y");
        b.Output("y", OnnxGraphBuilder.Float, 2, 2);
        var model = OnnxModel.Parse(b.Build("test"));
        var y = new OnnxSession(model).Run(new Dictionary<string, Tensor> { ["x"] = Tensor.Float(new[] { 2, 3 }, new float[] { 1, 0, 0, 0, 1, 0 }) })["y"];
        // rows of w: [1,2] and [3,4] -> softmax
        Assert.Equal(1f / (1f + MathF.Exp(1)), y.F[0], 5);
        Assert.Equal(1f / (1f + MathF.Exp(1)), y.F[2], 5);
    }
}

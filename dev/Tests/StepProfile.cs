using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Editor.Inference.Onnx;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

public class StepProfile
{
    readonly ITestOutputHelper _out;
    public StepProfile(ITestOutputHelper o) => _out = o;

    [Fact]
    public void ProfileBatch2Step()
    {
        if (!File.Exists(UniMateGraphTests.V2Weights)) return;
        var (prepare, step) = UniMateGraphTests.Build(Path.Combine(Path.GetTempPath(), "t2a-step-profile"), 2, 22, 60);
        using var z = UniMateCoreTests.Open("v2_onnx_ref.npz");
        var inputs = DenoiserOnnxTests.Inputs(z);
        Tensor Drop0(Tensor t) => t.WithShape(t.Shape.Skip(1).ToArray());
        var prepared = prepare.Run(new Dictionary<string, Tensor>
        {
            ["tpos"] = Drop0(inputs["tpos"]), ["tpos_parent"] = Drop0(inputs["tpos_parent"]),
            ["joint_rel"] = Drop0(inputs["joint_rel"]), ["graph_dist"] = Drop0(inputs["graph_dist"]),
            ["depth"] = Drop0(inputs["depth"]), ["spectral"] = Drop0(inputs["spectral"]), ["name_emb"] = Drop0(inputs["name_emb"]),
        });
        var x = inputs["x"].F;
        var feed = new Dictionary<string, Tensor>(prepared)
        {
            ["x"] = Tensor.Float(new[] { 2, 22, 12, 60 }, x.Concat(x).ToArray()),
            ["t"] = Tensor.Float(new[] { 2 }, new[] { 0.37f, 0.37f }),
            ["caption_emb"] = Tensor.Float(new[] { 2, 768 }),
        };
        step.Run(feed);
        step.Profile = new Dictionary<string, double>();
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        var gc0 = GC.CollectionCount(0); var gc2 = GC.CollectionCount(2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int runs = 5;
        for (var r = 0; r < runs; r++) step.Run(feed);
        _out.WriteLine($"B=2 step {sw.ElapsedMilliseconds / runs} ms, allocated {(GC.GetTotalAllocatedBytes(true) - alloc0) / runs / 1e6:0} MB/step, gc0 {(GC.CollectionCount(0) - gc0) / (float)runs:0.0}/step, gc2 {(GC.CollectionCount(2) - gc2) / (float)runs:0.0}/step");
        foreach (var kv in step.Profile.OrderByDescending(kv => kv.Value).Take(14)) _out.WriteLine($"  {kv.Key} {kv.Value / runs:0.0} ms");
    }
}

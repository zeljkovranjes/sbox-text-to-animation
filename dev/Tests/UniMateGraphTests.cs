using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Editor.Inference.Onnx;
using TextToAnimation.Editor.Inference.Runtime;
using TextToAnimation.Editor.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>The C#-built UniMate ONNX graphs (prepare + step) against the PyTorch reference (needs P:/D: dev caches).</summary>
public class UniMateGraphTests
{
    public const string V2Weights = @"P:\02-projects\unimate-cache\unimate_uniml3d_f60_v2\ema_fp32.safetensors";
    readonly ITestOutputHelper _out;
    public UniMateGraphTests(ITestOutputHelper output) => _out = output;

    public static (OnnxSession Prepare, OnnxSession Step) Build(string dir, int batch, int joints, int frames)
    {
        Directory.CreateDirectory(dir);
        var weights = Safetensors.Read(V2Weights);
        var builder = new UniMateGraphBuilder(weights, UniMateArch.V2);
        byte[] prep, step;
        using (var data = File.Create(Path.Combine(dir, "prepare.data"))) prep = builder.BuildPrepare(joints, data, "prepare.data");
        using (var data = File.Create(Path.Combine(dir, "step.data"))) step = builder.BuildStep(batch, joints, frames, data, "step.data");
        File.WriteAllBytes(Path.Combine(dir, "prepare.onnx"), prep);
        File.WriteAllBytes(Path.Combine(dir, "step.onnx"), step);
        return (OnnxSession.Load(Path.Combine(dir, "prepare.onnx")), OnnxSession.Load(Path.Combine(dir, "step.onnx")));
    }

    [Fact]
    public void CompactGraphsMatchReferenceVelocity()
    {
        if (!File.Exists(V2Weights)) return;
        var dir = Path.Combine(Path.GetTempPath(), "t2a-unimate-graph");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (prepare, step) = Build(dir, 1, 22, 60);
        _out.WriteLine($"build+load {watch.ElapsedMilliseconds} ms ({prepare.Model.Graph.Nodes.Count} + {step.Model.Graph.Nodes.Count} nodes)");
        using var z = UniMateCoreTests.Open("v2_onnx_ref.npz");
        var inputs = DenoiserOnnxTests.Inputs(z);
        Tensor Drop0(Tensor t) => t.WithShape(t.Shape.Skip(1).ToArray());
        var prepared = prepare.Run(new Dictionary<string, Tensor>
        {
            ["tpos"] = Drop0(inputs["tpos"]), ["tpos_parent"] = Drop0(inputs["tpos_parent"]),
            ["joint_rel"] = Drop0(inputs["joint_rel"]), ["graph_dist"] = Drop0(inputs["graph_dist"]),
            ["depth"] = Drop0(inputs["depth"]), ["spectral"] = Drop0(inputs["spectral"]), ["name_emb"] = Drop0(inputs["name_emb"]),
        });
        foreach (var (caption, expectedKey) in new[] { ("caption_emb", "v_cond"), ("zero", "v_uncond") })
        {
            var feed = new Dictionary<string, Tensor>(prepared)
            {
                ["x"] = inputs["x"], ["t"] = inputs["t"],
                ["caption_emb"] = caption == "zero" ? Tensor.Float(new[] { 1, 768 }) : inputs["caption_emb"],
            };
            step.Profile = new Dictionary<string, double>();
            for (var run = 0; run < 2; run++)
            {
                watch.Restart();
                var v = step.Run(feed)["v"];
                _out.WriteLine($"{expectedKey} run {watch.ElapsedMilliseconds} ms");
                var expected = z[expectedKey];
                var err = v.F.Zip(expected.Values, (a, b) => MathF.Abs(a - b)).Max();
                _out.WriteLine($"max abs error {err}");
                Assert.True(err < 2e-3f, $"{expectedKey} error {err}");
            }
            foreach (var kv in step.Profile.OrderByDescending(kv => kv.Value).Take(8)) _out.WriteLine($"  {kv.Key} {kv.Value / 2:0} ms");
        }
    }
}

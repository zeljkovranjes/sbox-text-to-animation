using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TextToAnimation.Editor.Inference.Onnx;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>The torch-exported UniMate denoiser run by the managed ONNX runtime (dev reference; needs D:).</summary>
public class DenoiserOnnxTests
{
    readonly ITestOutputHelper _out;
    public DenoiserOnnxTests(ITestOutputHelper output) => _out = output;

    public static Dictionary<string, Tensor> Inputs(UniMateCoreTests.Npz z)
    {
        Tensor F(string k) { var a = z[k]; return Tensor.Float(a.Shape, a.Values); }
        Tensor L(string k) { var a = z[k]; return Tensor.Int64(a.Shape, a.Values.Select(v => (long)v).ToArray()); }
        return new Dictionary<string, Tensor>
        {
            ["x"] = F("x"), ["t"] = F("t"), ["tpos"] = F("tpos"), ["tpos_parent"] = F("tpos_parent"),
            ["joint_rel"] = L("joint_rel"), ["graph_dist"] = L("graph_dist"), ["depth"] = L("depth"),
            ["spectral"] = F("spectral"), ["name_emb"] = F("name_emb"), ["caption_emb"] = F("caption_emb"),
        };
    }

    [Fact]
    public void ExportedV2GraphMatchesReference()
    {
        const string path = @"D:\99-scratch\unimate-fixtures\denoiser_v2.onnx";
        if (!File.Exists(path)) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var session = OnnxSession.Load(path);
        _out.WriteLine($"load {watch.ElapsedMilliseconds} ms, {session.Model.Graph.Nodes.Count} nodes");
        using var z = UniMateCoreTests.Open("v2_onnx_ref.npz");
        var inputs = Inputs(z);
        session.Profile = new Dictionary<string, double>();
        for (var i = 0; i < 2; i++)
        {
            watch.Restart();
            var v = session.Run(inputs)[session.OutputNames[0]];
            _out.WriteLine($"run {watch.ElapsedMilliseconds} ms");
            foreach (var kv in session.Profile.OrderByDescending(kv => kv.Value).Take(12)) _out.WriteLine($"  {kv.Key} {kv.Value:0} ms");
            session.Profile.Clear();
            var expected = z["v_cond"];
            var err = v.F.Zip(expected.Values, (a, b) => MathF.Abs(a - b)).Max();
            _out.WriteLine($"max abs error {err}");
            Assert.True(err < 1e-3f, $"error {err}");
        }
    }
}

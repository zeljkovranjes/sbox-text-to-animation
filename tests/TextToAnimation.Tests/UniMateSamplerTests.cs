using System;
using System.IO;
using System.Linq;
using TextToAnimation.EditorTools.Inference.Runtime;
using TextToAnimation.EditorTools.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>End-to-end UniMate sampling in C# against the PyTorch reference (needs the dev caches).</summary>
public class UniMateSamplerTests
{
    readonly ITestOutputHelper _out;
    public UniMateSamplerTests(ITestOutputHelper output) => _out = output;

    static readonly object Gate = new();
    static UniMateModel _model;

    /// <summary>A model folder in %TEMP% assembled like an installation (weight blob + T5 files).</summary>
    public static UniMateModel Model()
    {
        lock (Gate)
        {
            if (_model is not null) return _model;
            var files = new UniMateFiles(Path.Combine(Path.GetTempPath(), "t2a-unimate-model"));
            Directory.CreateDirectory(files.Directory);
            if (!WeightBlob.Exists(files.WeightBlob)) UniMateModel.WriteWeights(Safetensors.Read(UniMateGraphTests.V2Weights), files.WeightBlob);
            if (!File.Exists(files.T5Encoder)) File.Copy(@"D:\99-scratch\hf-cache\xenova\encoder_model.onnx", files.T5Encoder);
            if (!File.Exists(files.T5Tokenizer)) File.Copy(@"D:\99-scratch\hf-cache\hub\models--google--flan-t5-base\snapshots\7bcac572ce56db69c1ea7c8af255c5d7c9672fc2\tokenizer.json", files.T5Tokenizer);
            return _model = UniMateModel.Load(files);
        }
    }

    public static bool Available => File.Exists(UniMateGraphTests.V2Weights) && File.Exists(@"D:\99-scratch\hf-cache\xenova\encoder_model.onnx");

    static float[] FirstJoints(float[] padded, int joints, int padJoints) => padded.Take(joints * 12 * 60).ToArray();

    [Fact]
    public void EulerTextToMotionMatchesReference()
    {
        if (!Available) return;
        var model = Model();
        var skeleton = UniMateCoreTests.FixtureSkeleton();
        using var cond = UniMateCoreTests.Open("v2_cond.npz");
        // the reference used the stored (torch_geometric) spectral basis: use it for an exact comparison
        var spec = cond["spec"]; var sp = new float[22, 8];
        for (var j = 0; j < 22; j++) for (var c = 0; c < 8; c++) sp[j, c] = spec.Values[j * 8 + c];
        skeleton.SetSpectral(sp);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var prep = model.Prepare(skeleton, UniMateStats.Mixamo, default);
        _out.WriteLine($"prepare {watch.ElapsedMilliseconds} ms");
        var caption = cond["cond_caption_emb"].Values;
        using var reference = UniMateCoreTests.Open("v2_t2m_euler50_cfg3.npz");
        var noise = FirstJoints(reference["noise"].Values, 22, 71);
        watch.Restart();
        var x = model.Sample(prep, caption, noise, new SampleSettings { Steps = 50, Guidance = 3f }, null, default);
        _out.WriteLine($"sample 50 steps {watch.ElapsedMilliseconds} ms");
        var expected = FirstJoints(reference["x_final"].Values, 22, 71);
        var err = x.Zip(expected, (a, b) => MathF.Abs(a - b)).Max();
        var mean = x.Zip(expected, (a, b) => MathF.Abs(a - b)).Average();
        _out.WriteLine($"x_final max abs error {err}, mean {mean}");
        Assert.True(err < 0.02f, $"x_final error {err}");
    }
}

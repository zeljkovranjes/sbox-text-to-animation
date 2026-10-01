using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TextToAnimation.Editor.Inference.Runtime;
using TextToAnimation.Editor.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

public class CheckpointTests
{
    const string LocalCheckpoint = @"P:\02-projects\unimate-cache\unimate_uniml3d_f60_v2\checkpoints\checkpoint_step_100000.pt";
    readonly ITestOutputHelper _out;
    public CheckpointTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task ExtractsEmaWeightsMatchingReference()
    {
        if (!File.Exists(LocalCheckpoint) || !File.Exists(UniMateGraphTests.V2Weights)) return;
        var block = Path.Combine(Path.GetTempPath(), "t2a-ckpt-test", "ema.block");
        if (File.Exists(block)) File.Delete(block);
        long last = 0;
        var weights = await UniMateCheckpoint.FetchAsync(new FileRangeSource(LocalCheckpoint), block, (done, total) => last = total, default);
        _out.WriteLine($"block {last / 1e6:0.0} MB, {weights.Count} tensors");
        var reference = Safetensors.Read(UniMateGraphTests.V2Weights);
        foreach (var (name, t) in reference)
        {
            Assert.True(weights.ContainsKey(name), name);
            Assert.Equal(t.Shape, weights[name].Shape);
            Assert.True(t.Data.SequenceEqual(weights[name].Data), name);
        }
        Assert.InRange(last, 290_000_000, 340_000_000);
    }
}

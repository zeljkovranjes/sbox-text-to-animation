using System;
using System.Diagnostics;
using TextToAnimation.Editor.Inference.Onnx;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

public class GemmBench
{
    readonly ITestOutputHelper _out;
    public GemmBench(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Throughput()
    {
        var ctx = new ExecContext { MaxThreads = int.Parse(Environment.GetEnvironmentVariable("T2A_THREADS") ?? "15") };
        foreach (var (M, K, N) in new[] { (2684, 512, 1536), (2684, 512, 2730), (2684, 1365, 512), (21472, 64, 64) })
        {
            var rng = new Random(1);
            var a = new float[M * K]; var b = new float[K * N];
            for (var i = 0; i < a.Length; i++) a[i] = (float)rng.NextDouble() - .5f;
            for (var i = 0; i < b.Length; i++) b[i] = (float)rng.NextDouble() - .5f;
            var packed = FastKernels.Pack(b, K, N);
            var c = new float[M * N];
            FastKernels.Gemm(a, 0, M, packed, c, 0, ctx);
            var sw = Stopwatch.StartNew();
            const int reps = 10;
            for (var r = 0; r < reps; r++) FastKernels.Gemm(a, 0, M, packed, c, 0, ctx);
            var s = sw.Elapsed.TotalSeconds / reps;
            _out.WriteLine($"M{M} K{K} N{N}: {s * 1000:0.0} ms, {2.0 * M * N * K / s / 1e9:0} GFLOP/s");
            // spot check
            double expected = 0; for (var k = 0; k < K; k++) expected += a[5 * K + k] * b[k * N + 7];
            Assert.True(Math.Abs(expected - c[5 * N + 7]) < 1e-2);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TextToAnimation.Editor.Inference.Onnx;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The GPU plan run by a CPU emulator whose six kernels do exactly what the compute shaders do (same index
/// decoding, same parameter layout): the plan's launches, strides and buffer reuse are checked against the
/// normal executor without a GPU. The shaders themselves are checked in the editor gate.
/// </summary>
public class GpuPlanTests
{
    readonly ITestOutputHelper _out;
    public GpuPlanTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void EmulatedPlanMatchesTheCpuExecutor()
    {
        if (!File.Exists(UniMateGraphTests.V2Weights)) return;
        var (prepare, step) = UniMateGraphTests.Build(Path.Combine(Path.GetTempPath(), "t2a-gpu-plan"), 2, 22, 60);
        using var z = UniMateCoreTests.Open("v2_onnx_ref.npz");
        var inputs = DenoiserOnnxTests.Inputs(z);
        Tensor Drop0(Tensor t) => t.WithShape(t.Shape.Skip(1).ToArray());
        var prepared = prepare.Run(new Dictionary<string, Tensor>
        {
            ["tpos"] = Drop0(inputs["tpos"]), ["tpos_parent"] = Drop0(inputs["tpos_parent"]), ["joint_rel"] = Drop0(inputs["joint_rel"]),
            ["graph_dist"] = Drop0(inputs["graph_dist"]), ["depth"] = Drop0(inputs["depth"]), ["spectral"] = Drop0(inputs["spectral"]), ["name_emb"] = Drop0(inputs["name_emb"]),
        });
        var x = inputs["x"].F;
        var rng = new Random(3);
        var caption = Enumerable.Range(0, 2 * 768).Select(i => i < 768 ? (float)(rng.NextDouble() - 0.5) : 0f).ToArray();
        Dictionary<string, Tensor> Feed(float t) => new(prepared)
        {
            ["x"] = Tensor.Float(new[] { 2, 22, 12, 60 }, x.Concat(x.Select(v => v * 0.5f)).ToArray()),
            ["t"] = Tensor.Float(new[] { 2 }, new[] { t, t }),
            ["caption_emb"] = Tensor.Float(new[] { 2, 768 }, caption),
        };

        var recorder = new GpuPlan.Recorder();
        step.Trace = recorder.Record;
        step.Run(Feed(0.37f));
        step.Trace = null;
        var plan = GpuPlan.Build(step, recorder);
        Assert.True(plan is not null, GpuPlan.LastRefusal);
        var working = plan.Buffers.Where(b => b.Constant is null && b.Input is null).ToList();
        _out.WriteLine($"{plan.Launches.Count} launches ({string.Join(", ", plan.Launches.GroupBy(l => l.Kernel).Select(g => $"{g.Key} {g.Count()}"))}); " +
                       $"{working.Count} working buffers, {working.Sum(b => (long)b.Length) * 4 / 1e6:0.0} MB");

        // a different step (other t) through both paths: the plan must not depend on the traced values
        var feed = Feed(0.81f);
        var expected = step.Run(feed)["v"].F;
        var actual = Emulator.Run(plan, step, feed)["v"];
        var worst = 0f;
        for (var i = 0; i < expected.Length; i++) worst = MathF.Max(worst, MathF.Abs(expected[i] - actual[i]));
        _out.WriteLine($"max |cpu - emulated plan| = {worst:G3}");
        Assert.True(worst < 1e-3f, $"emulated plan differs by {worst}");
    }

    /// <summary>The compute shaders' arithmetic on the CPU (one method per shader).</summary>
    internal static class Emulator
    {
        public static Dictionary<string, float[]> Run(GpuPlan plan, OnnxSession session, IReadOnlyDictionary<string, Tensor> feed)
        {
            var buffers = plan.Buffers.Select(b =>
                b.Constant is not null ? session.Constant(b.Constant).F.ToArray()
                : b.Input is not null ? feed[b.Input].F.ToArray()
                : new float[b.Length]).ToArray();
            foreach (var l in plan.Launches)
            {
                var p = l.Params; var b = l.Buffers.Select(i => buffers[i]).ToArray();
                switch (l.Kernel)
                {
                    case GpuPlan.Kernel.Copy: Copy(p, b[0], b[1]); break;
                    case GpuPlan.Kernel.Elementwise: Elementwise(p, b[0], b[1], b[2], b[3]); break;
                    case GpuPlan.Kernel.Gemm: Gemm(p, b[0], b[1], b[2], b[3]); break;
                    case GpuPlan.Kernel.Attention: Attention(p, b[0], b[1], b[2], b[3], b[4]); break;
                    case GpuPlan.Kernel.RmsNorm: RmsNorm(p, b[0], b[1], b[2]); break;
                    case GpuPlan.Kernel.Rope: Rope(p, b[0], b[1], b[2], b[3]); break;
                }
            }
            return plan.Outputs.ToDictionary(kv => kv.Key, kv => buffers[kv.Value.Buffer].Take(kv.Value.Shape.Aggregate(1, (a, d) => a * d)).ToArray());
        }

        // P: count, srcBase, dstBase, shape[6], srcStride[6], dstStride[6]
        static void Copy(int[] p, float[] src, float[] dst)
        {
            Parallel.For(0, p[0], i =>
            {
                int rem = i, s = p[1], d = p[2];
                for (var k = GpuPlan.Rank - 1; k >= 0; k--)
                {
                    var n = p[3 + k]; var c = rem % n; rem /= n;
                    s += c * p[9 + k]; d += c * p[15 + k];
                }
                dst[d] = src[s];
            });
        }

        // P: count, mode, shape[6], strideA[6], strideB[6], strideC[6]
        static void Elementwise(int[] p, float[] a, float[] b, float[] c, float[] o)
        {
            Parallel.For(0, p[0], i =>
            {
                int rem = i, ia = 0, ib = 0, ic = 0;
                for (var k = GpuPlan.Rank - 1; k >= 0; k--)
                {
                    var n = p[2 + k]; var q = rem % n; rem /= n;
                    ia += q * p[8 + k]; ib += q * p[14 + k]; ic += q * p[20 + k];
                }
                var va = a[ia];
                o[i] = (GpuPlan.EltMode)p[1] switch
                {
                    GpuPlan.EltMode.Add => va + b[ib],
                    GpuPlan.EltMode.Mul => va * b[ib],
                    GpuPlan.EltMode.MulAdd => va * b[ib] + c[ic],
                    GpuPlan.EltMode.SiLU => va / (1f + MathF.Exp(-va)),
                    GpuPlan.EltMode.Sin => MathF.Sin(va),
                    GpuPlan.EltMode.Cos => MathF.Cos(va),
                    GpuPlan.EltMode.Sub => va - b[ib],
                    _ => va / b[ib],
                };
            });
        }

        // P: M, K, N, hasBias
        static void Gemm(int[] p, float[] a, float[] b, float[] bias, float[] c)
        {
            int M = p[0], K = p[1], N = p[2];
            Parallel.For(0, M, m =>
            {
                var row = new float[N];
                for (var k = 0; k < K; k++)
                {
                    var av = a[m * K + k];
                    for (var n = 0; n < N; n++) row[n] += av * b[k * N + n];
                }
                for (var n = 0; n < N; n++) c[m * N + n] = row[n] + (p[3] != 0 ? bias[n] : 0f);
            });
        }

        // P: rows, Hq, Hk, Sq, Sk, scale bits, hasMask, maskN, maskH, D - online softmax, one row per thread
        static void Attention(int[] p, float[] q, float[] k, float[] v, float[] mask, float[] o)
        {
            int Hq = p[1], Hk = p[2], Sq = p[3], Sk = p[4];
            var scale = BitConverter.Int32BitsToSingle(p[5]);
            var D = p[9];
            Parallel.For(0, p[0], g =>
            {
                var i = g % Sq; var nh = g / Sq; var n = nh / Hq; var h = nh % Hq; var hk = h / (Hq / Hk);
                int qb = g * D, kb = (n * Hk + hk) * Sk * D, mrow = n * p[7] + h * p[8] + i * Sk;
                var acc = new float[D];
                float mx = float.NegativeInfinity, l = 0f;
                for (var j = 0; j < Sk; j++)
                {
                    var s = 0f;
                    for (var d = 0; d < D; d++) s += q[qb + d] * k[kb + j * D + d];
                    s *= scale;
                    if (p[6] != 0) s += mask[mrow + j];
                    if (float.IsNegativeInfinity(s)) continue;
                    var mn = MathF.Max(mx, s);
                    var corr = MathF.Exp(mx - mn); var e = MathF.Exp(s - mn);
                    l = l * corr + e;
                    for (var d = 0; d < D; d++) acc[d] = acc[d] * corr + e * v[kb + j * D + d];
                    mx = mn;
                }
                var inv = l > 0 ? 1f / l : 0f;
                for (var d = 0; d < D; d++) o[qb + d] = acc[d] * inv;
            });
        }

        // P: rows, norm, eps bits, scale length
        static void RmsNorm(int[] p, float[] x, float[] g, float[] o)
        {
            int norm = p[1];
            var eps = BitConverter.Int32BitsToSingle(p[2]);
            Parallel.For(0, p[0], r =>
            {
                var ss = 0f;
                for (var i = 0; i < norm; i++) ss += x[r * norm + i] * x[r * norm + i];
                var inv = 1f / MathF.Sqrt(ss / norm + eps);
                for (var i = 0; i < norm; i++) o[r * norm + i] = x[r * norm + i] * inv * g[p[3] == 1 ? 0 : i];
            });
        }

        // P: count, d, positions, perm[d], sign[d]
        static void Rope(int[] p, float[] x, float[] cos, float[] sin, float[] o)
        {
            int d = p[1], positions = p[2];
            Parallel.For(0, p[0], e =>
            {
                var row = e / d; var i = e % d; var ob = row * d; var pb = row % positions * d;
                o[e] = x[ob + i] * cos[pb + i] + p[3 + d + i] * x[ob + p[3 + i]] * sin[pb + i];
            });
        }
    }
}

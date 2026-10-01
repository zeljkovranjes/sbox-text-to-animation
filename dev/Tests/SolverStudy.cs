using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TextToAnimation.Editor.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// How close each sampler gets to the converged solution (Euler, 100 steps) per model call - to pick the
/// fastest setting that is at least as accurate as the shipped one. Opt in: T2A_SOLVER_STUDY=1 (minutes).
/// </summary>
public class SolverStudy
{
    readonly ITestOutputHelper _out;
    public SolverStudy(ITestOutputHelper output) => _out = output;

    [Fact]
    public void CompareSolvers()
    {
        if (Environment.GetEnvironmentVariable("T2A_SOLVER_STUDY") != "1" || !UniMateSamplerTests.Available) return;
        var model = UniMateSamplerTests.Model();
        var skeleton = UniMateCoreTests.FixtureSkeleton();
        using var cond = UniMateCoreTests.Open("v2_cond.npz");
        var prep = model.Prepare(skeleton, UniMateStats.Mixamo, default);
        var caption = cond["cond_caption_emb"].Values;
        var results = new List<string>();
        foreach (var seed in new[] { 1, 2 })
        {
            var noise = UniMateModel.Noise(22, seed);
            var truth = model.Sample(prep, caption, noise, new SampleSettings { Steps = 100, Guidance = 3f }, null, default);
            float Rms(float[] x) => MathF.Sqrt(x.Zip(truth, (a, b) => (a - b) * (a - b)).Average());
            void Try(string name, SampleSettings s, int calls)
            {
                var w = Stopwatch.StartNew();
                var x = model.Sample(prep, caption, noise, s, null, default);
                var line = $"seed {seed} {name,-22} calls {calls,3}  rms {Rms(x):0.0000}  {w.ElapsedMilliseconds} ms";
                _out.WriteLine(line); results.Add(line);
            }
            foreach (var shift in new[] { 0.5f, 0.7f, 0.85f })
                foreach (var n in new[] { 8, 12, 16 })
                    Try($"ab2 {n} shift {shift}", new SampleSettings { Steps = n, Guidance = 3f, Method = Integrator.AdamsBashforth2, TimeShift = shift }, n);
            Try("euler 40", new SampleSettings { Steps = 40, Guidance = 3f }, 40);
            Try("ab2 24", new SampleSettings { Steps = 24, Guidance = 3f, Method = Integrator.AdamsBashforth2 }, 24);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// Which way a person flips (opt-in, T2A_BENCH=1: minutes of generation): the body's pitch (hips to head, in the plane
/// of the rig's forward and up) unwrapped over the clip; a front flip turns it about +360 degrees (the head goes
/// forward first), a backflip about -360. T2A_EVAL_PROMPTS (separated by '|') and T2A_EVAL_SEEDS pick the prompts.
/// </summary>
public class FlipBenchmark
{
    readonly ITestOutputHelper _out; public FlipBenchmark(ITestOutputHelper o) => _out = o;

    /// <summary>Hips travel along the rig's forward over the clip, in rest hip heights.</summary>
    public static float Travel(MotionRig rig, List<TextToAnimation.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var w0 = new TextToAnimation.Maths.XForm[s.Count]; var w1 = new TextToAnimation.Maths.XForm[s.Count];
        TextToAnimation.Processing.FkUtil.ToWorld(frames[0], s, w0); TextToAnimation.Processing.FkUtil.ToWorld(frames[^1], s, w1);
        return Vector3.Dot(w1[rig.HipsIndex].Pos - w0[rig.HipsIndex].Pos, rig.Forward) / s.RestWorld[rig.HipsIndex].Pos.Z;
    }

    public static float Pitch(MotionRig rig, List<TextToAnimation.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var hips = rig.HipsIndex; var head = rig.Analysis.SpineChain[^1];
        var w = new TextToAnimation.Maths.XForm[s.Count];
        float total = 0, last = float.NaN;
        foreach (var f in frames)
        {
            TextToAnimation.Processing.FkUtil.ToWorld(f, s, w);
            var d = w[head].Pos - w[hips].Pos;
            var a = MathF.Atan2(Vector3.Dot(d, rig.Forward), d.Z) * 180 / MathF.PI;
            if (!float.IsNaN(last)) { var step = a - last; if (step > 180) step -= 360; if (step < -180) step += 360; total += step; }
            last = a;
        }
        return total;
    }

    [Fact]
    public async Task Flips()
    {
        if (!UniMateSamplerTests.Available || Environment.GetEnvironmentVariable("T2A_BENCH") != "1") return;
        var prompts = (Environment.GetEnvironmentVariable("T2A_EVAL_PROMPTS") ?? "a person does a frontflip|An object performs a backflip.").Split('|');
        var seeds = (Environment.GetEnvironmentVariable("T2A_EVAL_SEEDS") ?? "11,22,33,44").Split(',').Select(int.Parse).ToArray();
        var gen = new UniMateGenerator(UniMateSamplerTests.Model());
        // T2A_EVAL_RIG=mixamo: UniMate's own Mixamo body (where the port matches upstream's output, DatasetMotionTests)
        var mixamo = Environment.GetEnvironmentVariable("T2A_EVAL_RIG") == "mixamo";
        var rig = mixamo ? PeopleBenchmark.MixamoRig() : Fixtures.HumanRig();
        foreach (var prompt in prompts)
        {
            var pitches = new List<float>(); var travels = new List<float>();
            foreach (var seed in seeds)
            {
                var r = (await gen.GenerateAsync(rig, new GenerationRequest { Mode = GenerationMode.TextToMotion, Prompts = new[] { prompt }, DurationSeconds = 2f, OutputFps = 30f, Seed = seed, RigFamily = RigFamily.Humanoid, CleanUp = false }, null, default)).Single();
                pitches.Add(Pitch(rig, r.Frames)); travels.Add(Travel(rig, r.Frames));
            }
            var line = $"{(mixamo ? "[mixamo] " : "")}{prompt} -> {UniMatePrompt.Caption(prompt)} | pitch {string.Join(" ", pitches.Select(p => $"{p:+0;-0}"))} | travel {string.Join(" ", travels.Select(t => $"{t:+0.0;-0.0}"))}" +
                $" | front {pitches.Count(p => p > 270)}, back {pitches.Count(p => p < -270)} of {pitches.Count}";
            _out.WriteLine(line);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "t2a_flips.txt"), line + "\n");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Inference.UniMate;
using TextToAnimation.Core.Generation;
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
    public static float Travel(MotionRig rig, List<TextToAnimation.Core.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var w0 = new TextToAnimation.Core.Maths.XForm[s.Count]; var w1 = new TextToAnimation.Core.Maths.XForm[s.Count];
        TextToAnimation.Core.Processing.FkUtil.ToWorld(frames[0], s, w0); TextToAnimation.Core.Processing.FkUtil.ToWorld(frames[^1], s, w1);
        return Vector3.Dot(w1[rig.HipsIndex].Pos - w0[rig.HipsIndex].Pos, rig.Forward) / s.RestWorld[rig.HipsIndex].Pos.Z;
    }

    /// <summary>Hips height over the clip relative to rest: its peak and its last frame (1 = standing).</summary>
    public static (float Peak, float End) Heights(MotionRig rig, List<TextToAnimation.Core.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var w = new TextToAnimation.Core.Maths.XForm[s.Count]; var rest = s.RestWorld[rig.HipsIndex].Pos.Z;
        float peak = float.MinValue, end = 0;
        foreach (var f in frames) { TextToAnimation.Core.Processing.FkUtil.ToWorld(f, s, w); end = w[rig.HipsIndex].Pos.Z / rest; peak = MathF.Max(peak, end); }
        return (peak, end);
    }

    public static float Pitch(MotionRig rig, List<TextToAnimation.Core.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var hips = rig.HipsIndex; var head = rig.Analysis.SpineChain[^1];
        var w = new TextToAnimation.Core.Maths.XForm[s.Count];
        float total = 0, last = float.NaN;
        foreach (var f in frames)
        {
            TextToAnimation.Core.Processing.FkUtil.ToWorld(f, s, w);
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
        var guidance = float.TryParse(Environment.GetEnvironmentVariable("T2A_EVAL_GUIDANCE"), System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 0f; // 0 = the default (3)
        // T2A_EVAL_RIG=mixamo: UniMate's own Mixamo body (where the port matches upstream's output, DatasetMotionTests)
        var mixamo = Environment.GetEnvironmentVariable("T2A_EVAL_RIG") == "mixamo";
        var rig = mixamo ? PeopleBenchmark.MixamoRig() : Fixtures.HumanRig();
        foreach (var prompt in prompts)
        {
            var pitches = new List<float>(); var travels = new List<float>(); var heights = new List<(float Peak, float End)>();
            foreach (var seed in seeds)
            {
                var r = (await gen.GenerateAsync(rig, new GenerationRequest { Mode = GenerationMode.TextToMotion, Prompts = new[] { prompt }, DurationSeconds = 2f, OutputFps = 30f, Seed = seed, RigFamily = RigFamily.Humanoid, CleanUp = false, Guidance = guidance }, null, default)).Single();
                pitches.Add(Pitch(rig, r.Frames)); travels.Add(Travel(rig, r.Frames)); heights.Add(Heights(rig, r.Frames));
            }
            // T2A_DUMP=<dir>: world joint positions of the first seed, raw and as the editor cleans it (for filmstrips)
            if (Environment.GetEnvironmentVariable("T2A_DUMP") is { Length: > 0 } dump)
                foreach (var clean in new[] { false, true })
                {
                    var r = (await gen.GenerateAsync(rig, new GenerationRequest { Mode = GenerationMode.TextToMotion, Prompts = new[] { prompt }, DurationSeconds = 2f, OutputFps = 30f, Seed = seeds[0], RigFamily = RigFamily.Humanoid, CleanUp = clean, Guidance = guidance }, null, default)).Single();
                    var w = new TextToAnimation.Core.Maths.XForm[rig.Skeleton.Count];
                    var frames = r.Frames.Select(f => { TextToAnimation.Core.Processing.FkUtil.ToWorld(f, rig.Skeleton, w); return w.Select(x => new[] { x.Pos.X, x.Pos.Y, x.Pos.Z }).ToArray(); }).ToArray();
                    var name = $"{(mixamo ? "mixamo" : "human")}_{string.Concat(prompt.Where(char.IsLetterOrDigit)).ToLowerInvariant()}_{(clean ? "clean" : "raw")}.json";
                    Directory.CreateDirectory(dump);
                    File.WriteAllText(Path.Combine(dump, name), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        parents = rig.Skeleton.Bones.Select(b => b.ParentIndex).ToArray(), names = rig.Skeleton.Bones.Select(b => b.Name).ToArray(),
                        forward = new[] { rig.Forward.X, rig.Forward.Y, rig.Forward.Z }, notes = r.Notes, frames,
                    }));
                }
            var line = $"{(mixamo ? "[mixamo] " : "")}{(guidance > 0 ? $"[cfg {guidance}] " : "")}{prompt} -> {UniMatePrompt.Caption(prompt)} | pitch {string.Join(" ", pitches.Select(p => $"{p:+0;-0}"))} | travel {string.Join(" ", travels.Select(t => $"{t:+0.0;-0.0}"))} | hips peak/end {string.Join(" ", heights.Select(h => $"{h.Peak:0.0}/{h.End:0.0}"))}" +
                $" | front {pitches.Count(p => p > 270)}, back {pitches.Count(p => p < -270)} of {pitches.Count}";
            _out.WriteLine(line);
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "t2a_flips.txt"), line + "\n");
        }
    }
}

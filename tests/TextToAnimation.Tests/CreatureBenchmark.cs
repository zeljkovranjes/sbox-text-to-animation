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
/// How close a creature's generated motion stands to UniMate's own motion of that animal (opt-in, T2A_BENCH=1:
/// minutes of generation) - the creature counterpart of <see cref="PeopleBenchmark"/>. The reference is UniMate's own
/// skeleton of the species from its training data (the port makes UniMate's motion there exactly:
/// DatasetMotionTests); the candidate is a rig as the editor gets it (engine skeleton dumped by the gate, with the
/// skin weights and model name the editor attached). Numbers are body-relative (UniMate joints, scaled by the rest
/// skeleton's leaf-to-leaf diameter), over several seeds; results are appended to %TEMP%/t2a_eval.txt.
/// </summary>
public class CreatureBenchmark
{
    readonly ITestOutputHelper _out;
    public CreatureBenchmark(ITestOutputHelper o) => _out = o;

    static string Prompt => Environment.GetEnvironmentVariable("T2A_EVAL_PROMPT") ?? "walk forward";
    static int[] Seeds => (Environment.GetEnvironmentVariable("T2A_EVAL_SEEDS") ?? "11,22,33").Split(',').Select(int.Parse).ToArray();

    public static Dictionary<string, float> Metrics(MotionRig rig, UniMateRig uni, List<TextToAnimation.Core.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var up = rig.Up;
        var J = uni.Count; var names = uni.Skeleton.CleanNames;
        var rest = s.RestWorld;
        var restPos = Enumerable.Range(0, J).Select(j => rest[uni.Bone[j]].Pos).ToList();
        var size = UniMateSkeleton.TreeDiameter(uni.Skeleton.Parents, restPos);
        bool IsFoot(string n) => n.EndsWith("Foot") || n.EndsWith("Toe") || n.EndsWith("Hand") || n.EndsWith("Paw") || n.EndsWith("Hoof");
        var feet = Enumerable.Range(0, J).Where(j => IsFoot(names[j])).ToList();
        var tails = Enumerable.Range(0, J).Where(j => names[j] == "Tail").ToList();
        float H(Vector3 p) => Vector3.Dot(p, up);
        Vector3 Flat(Vector3 p) => p - H(p) * up;
        var restGround = restPos.Min(H);
        var restHips = H(restPos[0]) - restGround;

        var w = new TextToAnimation.Core.Maths.XForm[s.Count];
        var hips = new List<float>(); var lift = new List<float>(); var hipFlat = new List<Vector3>();
        var footPos = new List<Vector3[]>(); var tailLat = new List<float>();
        foreach (var f in frames)
        {
            TextToAnimation.Core.Processing.FkUtil.ToWorld(f, s, w);
            var pos = Enumerable.Range(0, J).Select(j => w[uni.Bone[j]].Pos).ToArray();
            var ground = pos.Min(H);
            hips.Add((H(pos[0]) - ground) / size);
            // floating: the lowest foot above the engine floor (height 0 is where the rest pose stands)
            lift.Add(feet.Count > 0 ? (feet.Min(j => H(pos[j])) - restGround) / size : 0);
            hipFlat.Add(Flat(pos[0]));
            footPos.Add(feet.Select(j => pos[j]).ToArray());
            if (tails.Count > 0)
            {
                // the tail tip's sideways offset from the hips, across the body's heading
                var heading = Flat(pos[Math.Min(J - 1, 1)] - pos[0]);
                var side = heading.LengthSquared() > 1e-8f ? Vector3.Normalize(Vector3.Cross(up, heading)) : Vector3.UnitY;
                tailLat.Add(Vector3.Dot(pos[tails[^1]] - pos[0], side) / size);
            }
        }
        // planted-foot sliding: feet in their lowest fifth of height, horizontal speed against the body's speed
        var bodySpeed = Enumerable.Range(1, frames.Count - 1).Average(t => (hipFlat[t] - hipFlat[t - 1]).Length()) + 1e-6f;
        var slides = new List<float>();
        for (var k = 0; k < feet.Count; k++)
        {
            var heights = footPos.Select(p => H(p[k])).ToList();
            var low = heights.OrderBy(x => x).ElementAt(heights.Count / 5);
            for (var t = 1; t < frames.Count; t++)
                if (heights[t] <= low) slides.Add(Flat(footPos[t][k] - footPos[t - 1][k]).Length() / bodySpeed);
        }
        float Med(List<float> v) => v.Count == 0 ? 0 : v.OrderBy(x => x).ElementAt(v.Count / 2);
        float Range(List<float> v) => v.Count == 0 ? 0 : v.Max() - v.Min();
        return new()
        {
            ["hips"] = Med(hips), ["restHips"] = restHips / size, ["float"] = Med(lift),
            ["travel"] = (hipFlat[^1] - hipFlat[0]).Length() / size, ["slide"] = Med(slides),
            ["tailSwing"] = Range(tailLat),
        };
    }

    async Task Run(string label, MotionRig rig)
    {
        if (!UniMateSamplerTests.Available || Environment.GetEnvironmentVariable("T2A_BENCH") != "1") return;
        var uni = UniMateRig.Build(rig);
        var gen = new UniMateGenerator(UniMateSamplerTests.Model());
        var all = new List<Dictionary<string, float>>();
        foreach (var seed in Seeds)
        {
            var r = (await gen.GenerateAsync(rig, new GenerationRequest { Mode = GenerationMode.TextToMotion, Prompts = new[] { Prompt }, DurationSeconds = 2f, OutputFps = 30f, Seed = seed, CleanUp = false }, null, default)).Single();
            all.Add(Metrics(rig, uni, r.Frames));
        }
        static float Std(IEnumerable<float> v) { var a = v.ToArray(); var m = a.Average(); return MathF.Sqrt(a.Average(x => (x - m) * (x - m))); }
        var line = $"{uni.Count} joints, {uni.Family}: " + string.Join(", ", all[0].Keys.Select(k => $"{k} {all.Average(m => m[k]):0.000}±{Std(all.Select(m => m[k])):0.000}"));
        _out.WriteLine($"{label}: {line}");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "t2a_eval.txt"), $"{Prompt} | {label}: {line}\n");
    }

    static MotionRig Dataset(string key) => NamingAgreementTests.RigOf(NamingAgreementTests.Load().First(e => e.dataset == "truebones" && e.key == key));
    static MotionRig Engine(string file) => MotionRig.Create(EngineSkeletonTests.LoadEngineSkeleton(file));

    [Fact] public Task UniMateAlligator() => Run("UniMate's Alligator", Dataset("Alligator"));
    [Fact] public Task UserAlligator() => Run("user alligator (editor)", Engine("engine_skeleton_user_alligator.json"));
    [Fact] public Task UniMateCrocodile() => Run("UniMate's Crocodile", Dataset("Crocodile"));
    [Fact] public Task UserCrocodile() => Run("user crocodile (editor)", Engine("engine_skeleton_user_crocodile.json"));
    [Fact] public Task UniMateFox() => Run("UniMate's Fox", Dataset("Fox"));
    [Fact] public Task Fox() => Run("glTF fox (editor)", Engine("engine_skeleton_fox.json"));
}

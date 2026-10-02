using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// How a generated walk's feet move against its body (opt-in, T2A_BENCH=1 and T2A_DUMP=&lt;dir&gt;): every foot's world
/// position and the hips per frame, raw and as the editor cleans it, on the s&amp;box human and the fox.
/// T2A_EVAL_PROMPT / T2A_EVAL_SEEDS pick the prompt and seed.
/// </summary>
public class FootSyncBenchmark
{
    readonly ITestOutputHelper _out; public FootSyncBenchmark(ITestOutputHelper o) => _out = o;

    /// <summary>How the rig analysis reads every Truebones animal of UniMate's data (limbs, feet, symmetry).</summary>
    [Fact]
    public void DatasetAnimals()
    {
        if (Environment.GetEnvironmentVariable("T2A_BENCH") != "1") return;
        int noFeet = 0, total = 0;
        foreach (var e in NamingAgreementTests.Load().Where(e => e.dataset == "truebones"))
        {
            var rig = NamingAgreementTests.RigOf(e); var a = rig.Analysis; total++;
            if (rig.Feet.Count == 0) noFeet++;
            _out.WriteLine($"{e.key,-16} symmetric {a.Symmetric,-5} limbs {a.Limbs.Count,2} ({string.Join(" ", a.Limbs.GroupBy(l => l.Kind).Select(g => $"{g.Key}x{g.Count()}"))}) feet {rig.Feet.Count} humanoid {a.IsHumanoid}");
        }
        _out.WriteLine($"no feet: {noFeet}/{total}");
    }

    [Theory]
    [InlineData("human")]
    [InlineData("fox")]
    [InlineData("tb_Fox")]
    [InlineData("tb_Dog")]
    [InlineData("tb_Horse")]
    [InlineData("tb_Eagle")]
    [InlineData("tb_Bird")]
    [InlineData("spec_bird")]
    [InlineData("spider")]
    [InlineData("tb_Spider")]
    public async Task Dump(string which)
    {
        if (!UniMateSamplerTests.Available || Environment.GetEnvironmentVariable("T2A_BENCH") != "1") return;
        var only = Environment.GetEnvironmentVariable("T2A_EVAL_RIGS");
        if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(which)) return;
        var dump = Environment.GetEnvironmentVariable("T2A_DUMP");
        if (string.IsNullOrEmpty(dump)) return;
        var prompt = Environment.GetEnvironmentVariable("T2A_EVAL_PROMPT") ?? "An object walks forward.";
        var seed = int.Parse((Environment.GetEnvironmentVariable("T2A_EVAL_SEEDS") ?? "11").Split(',')[0]);
        // tb_<key>: a Truebones animal from UniMate's own data (the port matches upstream's output there)
        var rig = which == "human" ? Fixtures.HumanRig()
            : which.StartsWith("spec_") ? MotionRig.Create(Creatures.Build(RigAnalysisTests.SpecOf(which[5..])))
            : which.StartsWith("tb_") ? NamingAgreementTests.RigOf(NamingAgreementTests.Load().First(e => e.dataset == "truebones" && e.key == which[3..]))
            : MotionRig.Create(EngineSkeletonTests.LoadEngineSkeleton($"engine_skeleton_{which}.json"));
        var u = UniMateRig.Build(rig);
        _out.WriteLine($"{which}: {u.Family}, {u.Count} joints: {string.Join(", ", u.Skeleton.CleanNames)}");
        _out.WriteLine($"  limbs: {string.Join("; ", rig.Analysis.Limbs.Select(l => $"{l.Kind} {rig.Skeleton[l.Chain[0]].Name}..{rig.Skeleton[l.Chain[^1]].Name}"))}");
        _out.WriteLine($"  feet: {rig.Feet.Count}; problems: {string.Join(" | ", rig.Problems)}");
        _out.WriteLine($"  unimate: raw {string.Join(", ", u.Prep?.RawNames ?? Array.Empty<string>())}; grafts {u.Prep?.Grafts.Count}; parents {string.Join(",", u.Skeleton.Parents)}; facing {u.Prep?.FaceSource}");
        if (Environment.GetEnvironmentVariable("T2A_INFO_ONLY") == "1") return;
        var gen = new UniMateGenerator(UniMateSamplerTests.Model());
        Directory.CreateDirectory(dump);
        foreach (var clean in new[] { false, true })
        {
            var r = (await gen.GenerateAsync(rig, new GenerationRequest { Mode = GenerationMode.TextToMotion, Prompts = new[] { prompt }, DurationSeconds = 4f, OutputFps = 30f, Seed = seed, CleanUp = clean }, null, default)).Single();
            var w = new TextToAnimation.Maths.XForm[rig.Skeleton.Count];
            var feet = rig.Feet.Select(f => f.Ankle).ToArray();
            var frames = r.Frames.Select(f =>
            {
                TextToAnimation.Processing.FkUtil.ToWorld(f, rig.Skeleton, w);
                return feet.Append(rig.HipsIndex).Concat(u.Bone.Select(b => b < 0 ? rig.HipsIndex : b)).Select(b => new[] { w[b].Pos.X, w[b].Pos.Y, w[b].Pos.Z }).ToArray();
            }).ToArray();
            File.WriteAllText(Path.Combine(dump, $"feet_{which}_{(clean ? "clean" : "raw")}.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                feet = feet.Select(b => rig.Skeleton[b].Name).ToArray(), hips = rig.Skeleton[rig.HipsIndex].Name,
                planted = new[] { rig.LeftFoot?.Ankle ?? -1, rig.RightFoot?.Ankle ?? -1 }.Where(b => b >= 0).Select(b => rig.Skeleton[b].Name).ToArray(),
                forward = new[] { rig.Forward.X, rig.Forward.Y, rig.Forward.Z }, up = new[] { rig.Up.X, rig.Up.Y, rig.Up.Z },
                notes = r.Notes, frames, family = rig.Analysis.IsHumanoid ? "humanoid" : "creature",
                // after the feet and the hips: every joint UniMate animates, with its label
                joints = u.Skeleton.CleanNames, jointParents = u.Skeleton.Parents,
            }));
            _out.WriteLine($"{which} {(clean ? "clean" : "raw")}: {r.Frames.Count} frames, feet {string.Join(", ", feet.Select(b => rig.Skeleton[b].Name))}; {string.Join(" ", r.Notes)}");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Inference.UniMate;
using TextToAnimation.Core.Generation;
using TextToAnimation.Core.Rig;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// How close a person's generated motion stands to UniMate's own people (opt-in, T2A_BENCH=1: minutes of
/// generation): body-relative numbers (hand height, reach, arm extension, crouch, knee bend, upper-arm angle, hip
/// travel) over several seeds, on UniMate's Mixamo body (the port matches upstream there exactly) and the s&amp;box
/// human. T2A_EVAL_PROMPT / T2A_EVAL_SEEDS pick the prompt and seeds; results are appended to %TEMP%/t2a_eval.txt.
/// </summary>
public class PeopleBenchmark
{
    readonly ITestOutputHelper _out; public PeopleBenchmark(ITestOutputHelper o) => _out = o;
    public static string Prompt = Environment.GetEnvironmentVariable("T2A_EVAL_PROMPT") ?? "punch forward";
    static readonly int[] Seeds = (Environment.GetEnvironmentVariable("T2A_EVAL_SEEDS") ?? "11,22,33,44,55").Split(',').Select(int.Parse).ToArray();

    public static MotionRig MixamoRig()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream_dataset");
        var j = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "ds_mixamo_mixamo_walks.json"))).RootElement;
        using var z = new UniMateCoreTests.Npz(Path.Combine(dir, "ds_mixamo_mixamo_walks.npz"));
        var names = j.GetProperty("joint_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        var par = j.GetProperty("parents").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var J = names.Length; var basis = UniMateSkeleton.EngineCanonicalBasis;
        var tp = z["tpos"].Values; var tr = z["tpos_global_rot"].Values;
        var world = new TextToAnimation.Core.Maths.XForm[J];
        for (var u = 0; u < J; u++)
        {
            var canon = new Vector3(tp[u * 3], tp[u * 3 + 1], tp[u * 3 + 2]);
            var rc = M3.FromQuaternion(new Quaternion(tr[u * 4 + 1], tr[u * 4 + 2], tr[u * 4 + 3], tr[u * 4]));
            world[u] = new TextToAnimation.Core.Maths.XForm(basis.Transposed * canon * 40f, Quaternion.Normalize((basis.Transposed * rc * basis).ToQuaternion()));
        }
        var defs = Enumerable.Range(0, J).Select(u => new BoneDefinition(names[u], par[u] < 0 ? null : names[par[u]],
            par[u] < 0 ? world[u] : TextToAnimation.Core.Maths.XForm.ToLocal(world[par[u]], world[u]))).ToList();
        var sk = Skeleton.Create(defs);
        UniMateSkin.Attach(sk, null, "mixamo");
        return MotionRig.Create(sk);
    }

    public static Dictionary<string, float> Metrics(MotionRig rig, List<TextToAnimation.Core.Maths.XForm[]> frames)
    {
        var s = rig.Skeleton; var an = rig.Analysis;
        var arms = an.Limbs.Where(l => l.Kind == LimbKind.Arm).ToList();
        var legs = an.Limbs.Where(l => l.Kind == LimbKind.Leg).ToList();
        var hips = rig.HipsIndex; var chest = an.SpineChain.Count > 2 ? an.SpineChain[^3] : hips;
        var rest = s.RestWorld; float z0 = legs.SelectMany(l => l.Chain).Min(b => rest[b].Pos.Z), h = rest.Max(x => x.Pos.Z) - z0;
        var w = new TextToAnimation.Core.Maths.XForm[s.Count];
        var handH = new List<float>(); var reach = new List<float>(); var ext = new List<float>(); var drop = new List<float>(); var hipXY = new List<Vector3>();
        var knee = new List<float>(); var armDown = new List<float>();
        foreach (var f in frames)
        {
            TextToAnimation.Core.Processing.FkUtil.ToWorld(f, s, w);
            var ground = legs.SelectMany(l => l.Chain).Min(b => w[b].Pos.Z);
            foreach (var a in arms)
            {
                int up = a.Chain[^3], hand = a.Chain[^1];
                var len = (rest[a.Chain[^2]].Pos - rest[up].Pos).Length() + (rest[hand].Pos - rest[a.Chain[^2]].Pos).Length();
                handH.Add((w[hand].Pos.Z - ground) / h);
                reach.Add(Vector3.Dot(w[hand].Pos - w[chest].Pos, rig.Forward) / h);
                ext.Add((w[hand].Pos - w[up].Pos).Length() / len);
                var ad = Vector3.Normalize(w[a.Chain[^2]].Pos - w[up].Pos); armDown.Add(MathF.Asin(-ad.Z) * 180 / MathF.PI);
            }
            foreach (var l in legs)
            {
                var a = Vector3.Normalize(w[l.Chain[1]].Pos - w[l.Chain[0]].Pos); var b = Vector3.Normalize(w[l.Chain[2]].Pos - w[l.Chain[1]].Pos);
                knee.Add(MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / MathF.PI);
            }
            drop.Add((rest[hips].Pos.Z - z0 - (w[hips].Pos.Z - ground)) / h);
            hipXY.Add(w[hips].Pos with { Z = 0 });
        }
        float P(List<float> v, float q) { var o = v.OrderBy(x => x).ToList(); return o[Math.Min(o.Count - 1, (int)(q * o.Count))]; }
        return new()
        {
            ["handH"] = P(handH, .5f), ["reach95"] = P(reach, .95f), ["ext95"] = P(ext, .95f), ["crouch"] = P(drop, .5f),
            ["knee"] = P(knee, .5f), ["armDown"] = P(armDown, .5f), ["travel"] = (hipXY[^1] - hipXY[0]).Length() / h,
        };
    }

    async Task Run(string label, MotionRig rig, RigFamily family)
    {
        if (!UniMateSamplerTests.Available || Environment.GetEnvironmentVariable("T2A_BENCH") != "1") return;
        var gen = new UniMateGenerator(UniMateSamplerTests.Model());
        var all = new List<Dictionary<string, float>>();
        foreach (var seed in Seeds)
        {
            var r = (await gen.GenerateAsync(rig, new GenerationRequest { Mode = GenerationMode.TextToMotion, Prompts = new[] { Prompt }, DurationSeconds = 2f, OutputFps = 30f, Seed = seed, RigFamily = family, CleanUp = false }, null, default)).Single();
            all.Add(Metrics(rig, r.Frames));
        }
        var line = string.Join(", ", all[0].Keys.Select(k => $"{k} {all.Average(m => m[k]):0.00}±{Std(all.Select(m => m[k])):0.00}"));
        _out.WriteLine($"{label}: {line}");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "t2a_eval.txt"), $"{Prompt} | {label}: {line}\n");
    }
    static float Std(IEnumerable<float> v) { var a = v.ToArray(); var m = a.Average(); return MathF.Sqrt(a.Average(x => (x - m) * (x - m))); }

    [Fact] public Task Mixamo() => Run("UniMate's Mixamo body", MixamoRig(), RigFamily.Humanoid);
    [Fact] public Task Human() => Run("s&box human, as the editor prepares it", Fixtures.HumanRig(), RigFamily.Humanoid);
    [Fact] public Task HumanObjaverse() => Run("s&box human, Objaverse statistics", Fixtures.HumanRig(), RigFamily.Object);
}

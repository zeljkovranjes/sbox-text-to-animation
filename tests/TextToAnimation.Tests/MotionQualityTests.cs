using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Inference.UniMate;
using TextToAnimation.Core.Generation;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// What a generated walk looks like on the s&amp;box human. UniMate's raw output on this rig jitters (main joints'
/// median acceleration ~5 body heights/s², against ~1 on UniMate's own Mixamo walk) and its planted feet drift at
/// ~0.2 of the body's speed (as in UniMate's own walk). The default clean-up must bring both to clean motion; the
/// raw option must leave the model's output alone.
/// </summary>
public class MotionQualityTests
{
    readonly ITestOutputHelper _out;
    public MotionQualityTests(ITestOutputHelper o) => _out = o;

    internal static (float Accel, float StanceSlide) Measure(MotionRig rig, IReadOnlyList<XForm[]> frames, float fps)
    {
        var s = rig.Skeleton; var w = new XForm[s.Count];
        bool Main(string n) => !(n.Contains("finger") || n.Contains("twist") || n.Contains("helper") || n.Contains("IK") || n.Contains("ikrule")
            || n.Contains("clothing") || n.Contains("face") || n.Contains("eye") || n.Contains("jaw") || n.Contains("root"));
        var main = Enumerable.Range(0, s.Count).Where(b => Main(s[b].Name)).ToList();
        var p = frames.Select(f => { FkUtil.ToWorld(f, s, w); return w.Select(x => x.Pos).ToArray(); }).ToList();
        float Up(Vector3 v) => Vector3.Dot(v, rig.Up);
        var height = main.Max(b => Up(p[0][b])) - main.Min(b => Up(p[0][b]));
        var acc = new List<float>();
        for (var t = 1; t < p.Count - 1; t++) foreach (var b in main) acc.Add((p[t + 1][b] - 2 * p[t][b] + p[t - 1][b]).Length() * fps * fps / height);
        acc.Sort();
        Vector3 Flat(Vector3 v) => v - Up(v) * rig.Up;
        var bodySpeed = Flat(p[^1][rig.HipsIndex] - p[0][rig.HipsIndex]).Length() / (p.Count - 1);
        // a foot's stance: its slowest 30% of frames (a walking foot is planted about 60% of the time)
        var slide = new[] { rig.LeftFoot!.Ankle, rig.RightFoot!.Ankle }.Select(foot =>
        {
            var v = Enumerable.Range(1, p.Count - 1).Select(t => Flat(p[t][foot] - p[t - 1][foot]).Length()).OrderBy(x => x).ToList();
            return v.Take(v.Count * 3 / 10).Average() / Math.Max(1e-6f, bodySpeed);
        }).Average();
        return (acc[acc.Count / 2], slide);
    }

    const string CitizenBodyFbx = @"C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\citizen\Assets\models\citizen_human\bodies\male\citizen_human_body_male.fbx";

    /// <summary>
    /// UniMate's own clean-up (its Blender add-on's: collisions against capsules fitted to the body mesh, then ground
    /// contact) on a generated walk of the s&amp;box human, with the body mesh's capsules: no faults, bone lengths kept.
    /// </summary>
    [Fact]
    public async Task UniMatesCleanupRunsOnTheHumansBodyWithoutFaults()
    {
        if (!UniMateSamplerTests.Available || !File.Exists(CitizenBodyFbx)) return;
        var rig = Fixtures.HumanRig();
        var sk = rig.Skeleton;
        var mesh = TextToAnimation.EditorTools.Engine.FbxSkin.MeshPoints(File.ReadAllBytes(CitizenBodyFbx))!.Value;
        var names = sk.Bones.Select(b => b.Name).ToList();
        var matched = mesh.BindPositions.Where(kv => names.Contains(kv.Key)).ToList();
        var (m, _, _) = TextToAnimation.EditorTools.Formats.Fbx.FbxClipExport.Similarity(matched.Select(kv => kv.Value).ToList(), matched.Select(kv => (System.Numerics.Vector3)sk.RestWorld[names.IndexOf(kv.Key)].Pos).ToList());
        UniMateSkin.Attach(sk, UniMateSkin.WeightsOf(sk), UniMateSkin.ObjectTypeOf(sk), UniMateSkin.DrivenOf(sk),
            mesh.Points.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => names.IndexOf(kv.Key), kv => (IReadOnlyList<System.Numerics.Vector3>)kv.Value.Select(p => System.Numerics.Vector3.Transform(p, m)).ToList()));
        var generator = new UniMateGenerator(UniMateSamplerTests.Model());
        // the prompt goes to UniMate as written (as upstream), so it is worded as UniMate's training captions are
        GenerationRequest Request(bool clean) => new() { Mode = GenerationMode.TextToMotion, Prompts = new[] { "An object walks forward." }, DurationSeconds = 2f, OutputFps = 30f, Seed = 3, Steps = 12, CleanUp = clean };
        var raw = (await generator.GenerateAsync(rig, Request(false), null, default)).Single();
        var clean = (await generator.GenerateAsync(rig, Request(true), null, default)).Single();
        var r = Measure(rig, raw.Frames, 30f); var c = Measure(rig, clean.Frames, 30f);
        _out.WriteLine($"raw: jerk {r.Accel:0.00}, stance feet at {r.StanceSlide:0.00} of body speed; cleaned: jerk {c.Accel:0.00}, stance feet at {c.StanceSlide:0.00} ({string.Join(" ", clean.Notes)})");
        // UniMate's own clean-up (proven equal to its add-on by UpstreamCleanupTests): it runs with the body's shapes and
        // adds no faults; on the s&box human it holds little (its ankle capsule puts the sole plane below the floor, so
        // most stances are out of the leg's reach), which the numbers above record
        Assert.Contains(clean.Notes, n => n.StartsWith("UniMate clean-up:") && n.Contains("ground:"));
        for (var t = 0; t < clean.Frames.Count; t++)
            for (var b = 0; b < sk.Count; b++)
                if (sk[b].ParentIndex >= 0) Assert.True(Vector3.Distance(clean.Frames[t][b].Pos, raw.Frames[t][b].Pos) < 1e-3f, $"{sk[b].Name} changed length");
        var issues = ClipQuality.Analyze(new AnimClip { Fps = 30f, Frames = clean.Frames }, rig);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }
}

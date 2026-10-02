using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
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

    [Fact]
    public async Task AGeneratedWalkIsSmoothAndKeepsItsFeetPlanted()
    {
        if (!UniMateSamplerTests.Available) return;
        var rig = Fixtures.HumanRig();
        var generator = new UniMateGenerator(UniMateSamplerTests.Model());
        // the prompt goes to UniMate as written (as upstream), so it is worded as UniMate's training captions are
        GenerationRequest Request(bool clean) => new() { Mode = GenerationMode.TextToMotion, Prompts = new[] { "An object walks forward." }, DurationSeconds = 2f, OutputFps = 30f, Seed = 3, Steps = 12, CleanUp = clean };
        var raw = (await generator.GenerateAsync(rig, Request(false), null, default)).Single();
        var clean = (await generator.GenerateAsync(rig, Request(true), null, default)).Single();
        var r = Measure(rig, raw.Frames, 30f); var c = Measure(rig, clean.Frames, 30f);
        _out.WriteLine($"raw: jerk {r.Accel:0.00}, stance feet at {r.StanceSlide:0.00} of body speed; cleaned: jerk {c.Accel:0.00}, stance feet at {c.StanceSlide:0.00} ({string.Join(" ", clean.Notes)})");
        Assert.True(c.StanceSlide < 0.05f, $"planted feet still slide at {c.StanceSlide:0.00} of the body's speed");
        Assert.True(c.Accel < 2.5f, $"the walk still jitters ({c.Accel:0.00})");
        var issues = ClipQuality.Analyze(new AnimClip { Fps = 30f, Frames = clean.Frames }, rig);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Empty(raw.Notes.Where(n => n.StartsWith("Smoothed")));
    }
}

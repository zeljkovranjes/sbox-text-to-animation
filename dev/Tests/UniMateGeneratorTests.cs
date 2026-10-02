using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

public class UniMatePromptTests
{
    // the prompt as written, as upstream (sample.py: prompt.strip()), however it is worded
    [Theory]
    [InlineData("  a person runs and then performs a front flip  ", "a person runs and then performs a front flip")]
    [InlineData("Walk cautiously forward, look behind, then run.", "Walk cautiously forward, look behind, then run.")]
    [InlineData("a tired man slowly walk home", "a tired man slowly walk home")]
    [InlineData("An object runs, jumps, rolls, and then stands up.", "An object runs, jumps, rolls, and then stands up.")]
    public void Captions(string input, string expected) => Assert.Equal(expected, UniMatePrompt.Caption(input));

    /// <summary>Caption strips exactly what Python's str.strip() does (its str.isspace set over the whole BMP).</summary>
    [Fact]
    public void StripsLikePython()
    {
        var python = new[] { 0x9, 0xa, 0xb, 0xc, 0xd, 0x1c, 0x1d, 0x1e, 0x1f, 0x20, 0x85, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004,
            0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000 };
        var stripped = Enumerable.Range(0, 0x10000).Where(c => c is < 0xd800 or > 0xdfff && UniMatePrompt.Caption($"{(char)c}x{(char)c}") == "x").ToArray();
        Assert.Equal(python, stripped);
    }

    [Theory]
    [InlineData("A human does a front flip.", "A human does a front flip")]
    [InlineData("walk forward", "Walk forward")]
    [InlineData("Walk cautiously forward, look behind, then run.", "Walk cautiously forward, look behind, then run")]
    [InlineData("crouch down low and sneak slowly past the sleeping guard without making noise", "Crouch down low and sneak slowly past the")]
    [InlineData("", "Generated")]
    [InlineData("   \n\t ", "Generated")]
    [InlineData("jump\nthen   land\r\n", "Jump then land")]
    [InlineData("🕺 dance", "🕺 dance")]
    [InlineData("An object walks forward.", "Walks forward")]
    [InlineData("an  OBJECT runs, jumps, rolls, and then stands up.", "Runs, jumps, rolls, and then stands up")]
    [InlineData("An object", "An object")]
    [InlineData("An objective look", "An objective look")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa🕺🕺", "Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ClipNames(string prompt, string expected) => Assert.Equal(expected, UniMatePrompt.ClipName(prompt));
}

public class UniMateGeneratorTests
{
    readonly ITestOutputHelper _out;
    public UniMateGeneratorTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task GeneratesAPlausibleWalkOnTheSboxHuman()
    {
        if (!UniMateSamplerTests.Available) return;
        var rig = Fixtures.HumanRig();
        var generator = new UniMateGenerator(UniMateSamplerTests.Model());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = await generator.GenerateAsync(rig, new GenerationRequest
        {
            Mode = GenerationMode.TextToMotion, Prompts = new[] { "walk forward" }, DurationSeconds = 2f,
            OutputFps = 30f, Seed = 3, Steps = 16,
        }, null, default);
        _out.WriteLine($"generated in {watch.ElapsedMilliseconds} ms");
        var motion = results.Single();
        Assert.InRange(motion.Frames.Count, 59, 61);
        var clip = new AnimClip { Frames = motion.Frames, Fps = 30 };
        ClipOps.Finish(clip, rig);
        var issues = ClipQuality.Analyze(clip, rig);
        foreach (var i in issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        // a walk travels: hips move at least ~30 cm over two seconds, roughly along the rig's forward axis
        var path = RootTools.HipsTrajectory(clip.Frames, rig);
        var travel = RootTools.Horizontal(rig, path[^1] - path[0]);
        _out.WriteLine($"travel {travel / rig.UnitsPerCm} cm");
        Assert.True(travel.Length() > rig.Cm(30f), $"walk travelled only {travel.Length() / rig.UnitsPerCm:0} cm");
        // and the character stays upright with the hips near their rest height
        var restHips = rig.Skeleton.RestWorld[rig.HipsIndex].Pos.Z;
        Assert.InRange(path.Average(p => p.Z), restHips * 0.75f, restHips * 1.2f);
        // wrists and fingers: UniMate's people have no finger joints and no hand rotation (the hand ends the arm),
        // so hands and fingers keep the model's own relation to the forearm - never the T-pose UniMate was shown
        var s = rig.Skeleton;
        var held = Enumerable.Range(0, s.Count).Where(b => s[b].Name is "hand_L" or "hand_R" || s[b].Name.StartsWith("finger_")).ToList();
        Assert.True(held.Count > 20);
        var worst = held.Max(b => motion.Frames.Max(f => TextToAnimation.Maths.MathQ.AngleBetween(f[b].Rot, s[b].RestLocal.Rot))) * 180 / MathF.PI;
        _out.WriteLine($"hands and fingers off their rest relation by up to {worst:0.000} deg");
        Assert.True(worst < 0.01f, $"a hand or finger turned {worst} deg against its parent");
    }
}

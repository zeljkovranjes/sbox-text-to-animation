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
    [Theory]
    [InlineData("Walk cautiously forward", "An object walks cautiously forward.")]
    [InlineData("a person jumps", "An object jumps.")]
    [InlineData("crouch", "An object crouches.")]
    [InlineData("slowly wave hello", "An object slowly waves hello.")]
    [InlineData("An object runs.", "An object runs.")]
    [InlineData("cry", "An object cries.")]
    [InlineData("a bipedal punches forward", "An object punches forward.")]
    [InlineData("A dangerous robot in armor swings its arm", "An object swings its arm.")]
    [InlineData("my character kicks with the right leg", "An object kicks with the right leg.")]
    [InlineData("a person punch forward", "An object punches forward.")]
    [InlineData("the boss walks", "An object walks.")]
    [InlineData("a human punch forward", "An object punches forward.")]
    [InlineData("A human does a front flip.", "An object does a front flip.")]
    [InlineData("a cool robot jump over a box", "An object jumps over a box.")]
    [InlineData("my knight swing a sword", "An object swings a sword.")]
    [InlineData("a duck walks", "An object walks.")]
    [InlineData("a person back flips", "An object back flips.")]
    [InlineData("a tired man slowly walk home", "An object slowly walks home.")]
    public void Captions(string input, string expected) => Assert.Equal(expected, UniMatePrompt.ToCaption(input));

    [Theory]
    [InlineData("A human does a front flip.", "Does a front flip")]
    [InlineData("walk forward", "Walk forward")]
    [InlineData("Walk cautiously forward, look behind, then run.", "Walk cautiously forward")]
    [InlineData("a human punch forward", "Punches forward")]
    [InlineData("An object flaps its wings and rises.", "Flaps its wings and rises")]
    [InlineData("crouch down low and sneak slowly past the sleeping guard without making noise", "Crouch down low and sneak slowly past the")]
    [InlineData("", "Generated")]
    public void ClipNames(string prompt, string expected) => Assert.Equal(expected, UniMatePrompt.ClipName(prompt));

    [Fact]
    public void SplitsSequentialPrompts()
    {
        Assert.Equal(new[] { "Walk cautiously forward", "look behind", "run" }, UniMatePrompt.SplitSteps("Walk cautiously forward, look behind, then run."));
        Assert.Single(UniMatePrompt.SplitSteps("A person waves while walking forward"));
    }
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

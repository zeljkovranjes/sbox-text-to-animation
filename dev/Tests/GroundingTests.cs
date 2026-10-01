using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// Generated motion stands on the ground. The network's output is only right under the statistics of the dataset a
/// skeleton resembles (upstream normalises each skeleton with its own dataset's): a person normalised with the
/// Objaverse statistics walks inches above the floor the whole clip.
/// </summary>
public class GroundingTests
{
    readonly ITestOutputHelper _out;
    public GroundingTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void APersonUsesThePeopleStatistics()
    {
        Assert.Equal(RigFamily.Humanoid, UniMateRig.DetectFamily(Fixtures.HumanRig()));
        Assert.Equal(RigFamily.Animal, UniMateRig.DetectFamily(TextToAnimation.Animation.MotionRig.Create(Creatures.Build(Creatures.Dog()))));
    }

    [Fact]
    public async Task AGeneratedWalkKeepsItsFeetOnTheGround()
    {
        if (!UniMateSamplerTests.Available) return;
        var rig = Fixtures.HumanRig();
        var s = rig.Skeleton;
        var generator = new UniMateGenerator(UniMateSamplerTests.Model());
        var walk = (await generator.GenerateAsync(rig, new GenerationRequest
        {
            Mode = GenerationMode.TextToMotion, Prompts = new[] { "walk forward" }, DurationSeconds = 2f, OutputFps = 30f, Seed = 3, Steps = 12,
        }, null, default)).Single();
        int left = rig.LeftFoot!.Ankle, right = rig.RightFoot!.Ankle;
        float Height(XForm[] w, int b) => Vector3.Dot(w[b].Pos, rig.Up);
        var ground = MathF.Min(Height(s.RestWorld.ToArray(), left), Height(s.RestWorld.ToArray(), right));
        var world = new XForm[s.Count];
        var lowest = new List<float>();
        foreach (var frame in walk.Frames)
        {
            FkUtil.ToWorld(frame, s, world);
            lowest.Add(MathF.Min(Height(world, left), Height(world, right)) - ground);
        }
        lowest.Sort();
        var median = lowest[lowest.Count / 2];
        _out.WriteLine($"lowest ankle above the rest ground: {lowest[0]:0.0}..{lowest[^1]:0.0} in, median {median:0.0} in");
        Assert.True(MathF.Abs(median) < 1.5f, $"the walk floats: the lower foot is {median:0.0} in off the ground on a typical frame");
        Assert.True(lowest[0] < 1f, "no foot ever reaches the ground");
    }
}

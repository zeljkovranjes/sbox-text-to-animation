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
using TextToAnimation.Rig;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>UniMate rigs, clip tools and generation on armatures that aren't humanoid (and on renamed humans).</summary>
public class AnyArmatureTests
{
    readonly ITestOutputHelper _out;
    public AnyArmatureTests(ITestOutputHelper o) => _out = o;

    public static TheoryData<string> CreatureNames => RigAnalysisTests.CreatureNames;

    static MotionRig Rig(string creature) => MotionRig.Create(Creatures.Build(RigAnalysisTests.SpecOf(creature)));

    /// <summary>A plausible procedural motion for any rig: travel forward, bob, and swing every limb about the body's left axis.</summary>
    public static AnimClip Walk(MotionRig rig, int frames = 61, float fps = 30f)
    {
        var s = rig.Skeleton;
        var a = rig.Analysis;
        var rest = s.Bones.Select(b => b.RestLocal).ToArray();
        var clip = new AnimClip { Name = "walk", Fps = fps, Origin = ClipOrigin.Generated };
        for (var f = 0; f < frames; f++)
        {
            var t = f / fps;
            var phase = t * MathF.PI * 2f;
            var frame = rest.ToArray();
            var world = new XForm[s.Count];
            FkUtil.ToWorld(frame, s, world);
            var i = 0;
            foreach (var limb in a.Limbs)
            {
                var b = limb.Chain[0];
                var parent = s[b].ParentIndex;
                var swing = Quaternion.CreateFromAxisAngle(a.Left, MathF.Sin(phase + i++ * 1.3f) * 0.4f);
                var parentRot = parent < 0 ? Quaternion.Identity : world[parent].Rot;
                var local = Quaternion.Conjugate(parentRot) * swing * parentRot;
                frame[b] = new XForm(frame[b].Pos, Quaternion.Normalize(local * frame[b].Rot));
            }
            var root = frame[rig.RootIndex];
            frame[rig.RootIndex] = new XForm(root.Pos + rig.Forward * rig.Cm(80f) * t + rig.Up * MathF.Sin(phase * 2f) * rig.Cm(1.5f), root.Rot);
            clip.Frames.Add(frame);
        }
        ClipOps.Finish(clip, rig);
        return clip;
    }

    [Theory]
    [MemberData(nameof(CreatureNames))]
    public void BuildsAUniMateRigForEveryCreature(string creature)
    {
        var rig = Rig(creature);
        var u = UniMateRig.Build(rig);
        var s = rig.Skeleton;
        _out.WriteLine($"{creature}: {u.Count} joints, family {u.Family}, facing {u.Skeleton.RightHip}/{u.Skeleton.LeftHip} body axis {u.Skeleton.BodyAxis}");
        _out.WriteLine(string.Join(", ", u.Skeleton.CleanNames));
        Assert.InRange(u.Count, 5, UniMateRig.MaxJoints);
        Assert.Equal(RigFamily.Animal, u.Family);
        Assert.Equal(rig.Analysis.BodyRoot, u.RootBone);
        Assert.All(u.Bone.Where(b => b >= 0), b => Assert.True(rig.Analysis.InBody[b]));
        Assert.All(u.Skeleton.CleanNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.True(u.Skeleton.RightHip >= 0 && u.Skeleton.LeftHip >= 0, "every creature here has face joints");
        Assert.Equal(creature == "snake", u.Skeleton.BodyAxis);
        // the canonical rest faces +Z with Y up: the face pair lies across X (or the body along Z)
        var across = u.Skeleton.TPose[u.Skeleton.RightHip] - u.Skeleton.TPose[u.Skeleton.LeftHip];
        if (u.Skeleton.BodyAxis) Assert.True(across.Z > 0.9f * across.Length(), $"head-tail {across}");
        else Assert.True(across.X < -0.9f * across.Length(), $"right-left {across}");
    }

    [Theory]
    [MemberData(nameof(CreatureNames))]
    public void UniMateRigDoesNotDependOnNamesOrBoneOrder(string creature)
    {
        var spec = RigAnalysisTests.SpecOf(creature);
        var rename = Creatures.Gibberish(spec.Select(x => x.Name));
        AssertSameUniMateRig(MotionRig.Create(Creatures.Build(spec)), MotionRig.Create(Creatures.Build(spec, rename, 31)), rename);
    }

    [Fact]
    public void RenamedHumanGetsTheSameUniMateRig()
    {
        var named = Fixtures.HumanRig();
        var (skeleton, rename) = RigAnalysisTests.RenamedHuman();
        var renamed = MotionRig.Create(skeleton);
        Assert.True(renamed.IsHumanoid);
        Assert.Equal(2, renamed.Feet.Count);
        Assert.True(Vector3.Dot(renamed.Forward, Vector3.UnitX) > 0.99f);
        Assert.Equal(rename("pelvis"), renamed.Skeleton[renamed.HipsIndex].Name);
        AssertSameUniMateRig(named, renamed, rename);
        Assert.Equal(RigFamily.Humanoid, UniMateRig.Build(renamed).Family);
    }

    static void AssertSameUniMateRig(MotionRig a, MotionRig b, Func<string, string> rename)
    {
        var ua = UniMateRig.Build(a);
        var ub = UniMateRig.Build(b);
        Assert.Equal(ua.Count, ub.Count);
        Assert.Equal(ua.Bone.Select(x => x < 0 ? null : rename(a.Skeleton[x].Name)), ub.Bone.Select(x => x < 0 ? null : b.Skeleton[x].Name));
        Assert.Equal(ua.Skeleton.CleanNames, ub.Skeleton.CleanNames);
        Assert.Equal(ua.Skeleton.Parents, ub.Skeleton.Parents);
        Assert.Equal(ua.Skeleton.RightHip, ub.Skeleton.RightHip);
        Assert.Equal(ua.Skeleton.LeftHip, ub.Skeleton.LeftHip);
        Assert.Equal(ua.Family, ub.Family);
        for (var j = 0; j < ua.Count; j++)
            Assert.True(Vector3.Distance(ua.Skeleton.TPose[j], ub.Skeleton.TPose[j]) < 1e-5f, $"joint {j} {ua.Skeleton.CleanNames[j]}");
    }

    [Fact]
    public void LongRigsAreTrimmedToTheJointBudget()
    {
        var rig = Rig("dragon");
        Assert.True(rig.Analysis.BodySubtree(rig.Analysis.BodyRoot).Count() > UniMateRig.MaxJoints);
        var u = UniMateRig.Build(rig);
        Assert.InRange(u.Count, 5, UniMateRig.MaxJoints);
        var names = u.Skeleton.CleanNames;
        Assert.Contains("Head", names);
        Assert.Contains("Left Wing", names);
        Assert.Contains("Right Thigh", names);
        Assert.Contains("Tail", names);
    }

    [Theory]
    [MemberData(nameof(CreatureNames))]
    public void ClipToolsWorkOnEveryCreature(string creature)
    {
        var rig = Rig(creature);
        var clip = Walk(rig);
        var issues = ClipQuality.Analyze(clip, rig);
        foreach (var i in issues) _out.WriteLine($"{i.Severity} {i.Code}: {i.Message}");
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);

        var copy = clip.CloneDeep();
        ClipOps.MakeInPlace(copy, rig);
        var path = RootTools.HipsTrajectory(copy.Frames, rig);
        Assert.True(RootTools.Horizontal(rig, path[^1] - path[0]).Length() < rig.Cm(10f), "in place");
        ClipOps.RemoveRootDrift(clip.CloneDeep(), rig);
        ClipOps.MakeSeamlessLoop(clip.CloneDeep(), rig, 8);
        ClipOps.Reverse(clip.CloneDeep(), rig);
        ClipOps.TimeScale(clip.CloneDeep(), rig, 1.5f);
        ClipOps.Resample(clip.CloneDeep(), rig, 24f);
        ClipOps.OffsetRoot(clip.CloneDeep(), rig, rig.Forward * 10f, 45f, progressive: true);

        var hasFeet = rig.LeftFoot is not null && rig.RightFoot is not null;
        Assert.Equal(creature != "snake", hasFeet);
        var sliding = ClipCleanup.CleanFootSliding(clip.CloneDeep(), rig);
        Assert.Equal(hasFeet, sliding is not null);
        ClipCleanup.GroundFeet(clip.CloneDeep(), rig);
        ClipCleanup.GenerateFootsteps(clip.CloneDeep(), rig);

        var mirrored = ClipCleanup.MirrorFrames(clip.Frames, rig, out var error);
        Assert.True(mirrored is not null || creature == "snake", error);
        if (creature == "snake") Assert.Null(mirrored);
    }

    [Theory]
    [InlineData("dog")]
    [InlineData("bird")]
    [InlineData("human")]
    public void MirrorsRigsWithGibberishNamesFromTheirShape(string creature)
    {
        Func<string, string> rename;
        MotionRig rig;
        if (creature == "human") { var (sk, rn) = RigAnalysisTests.RenamedHuman(); rig = MotionRig.Create(sk); rename = rn; }
        else
        {
            var spec = RigAnalysisTests.SpecOf(creature);
            rename = Creatures.Gibberish(spec.Select(x => x.Name));
            rig = MotionRig.Create(Creatures.Build(spec, rename, 5));
        }
        var clip = Walk(rig);
        var mirrored = ClipCleanup.MirrorFramesByShape(clip.Frames, rig, out var error);
        Assert.True(mirrored is not null, error);
        Assert.NotNull(ClipCleanup.MirrorFrames(clip.Frames, rig, out _));
        var twice = ClipCleanup.MirrorFramesByShape(mirrored, rig, out _);
        for (var f = 0; f < clip.FrameCount; f += 10)
            for (var b = 0; b < rig.Skeleton.Count; b++)
            {
                Assert.True(Vector3.Distance(twice[f][b].Pos, clip.Frames[f][b].Pos) < 1e-4f);
                Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(twice[f][b].Rot, clip.Frames[f][b].Rot)) - 1f) < 1e-5f);
            }
        // the left limb now does what the right limb did, reflected across the body
        var a = rig.Analysis;
        var right = a.Limbs.First(l => l.Side == BoneSide.Right).Chain[^1];
        var left = a.Mirror[right];
        var wa = new XForm[rig.Skeleton.Count]; var wb = new XForm[rig.Skeleton.Count];
        FkUtil.ToWorld(clip.Frames[17], rig.Skeleton, wa);
        FkUtil.ToWorld(mirrored[17], rig.Skeleton, wb);
        var reflected = wa[right].Pos - 2f * Vector3.Dot(wa[right].Pos, a.MirrorNormal) * a.MirrorNormal;
        Assert.True(Vector3.Distance(reflected, wb[left].Pos) < 1e-3f, $"{reflected} vs {wb[left].Pos}");
    }

    /// <summary>On the s&amp;box human, pairing bones by shape mirrors exactly like pairing them by name (twist bones and IK helpers included).</summary>
    [Fact]
    public void ShapeMirrorMatchesNameMirrorOnTheSboxHuman()
    {
        var rig = Fixtures.HumanRig();
        var clip = Fixtures.Walk(rig);
        var byName = ClipMirror.Mirror(clip.Frames, rig.Rig);
        var byShape = ClipCleanup.MirrorFramesByShape(clip.Frames, rig, out var error);
        Assert.True(byShape is not null, error);
        var wa = new XForm[rig.Skeleton.Count]; var wb = new XForm[rig.Skeleton.Count];
        var worst = 0f; var worstBone = "";
        for (var f = 0; f < clip.FrameCount; f += 5)
        {
            FkUtil.ToWorld(byName[f], rig.Skeleton, wa);
            FkUtil.ToWorld(byShape[f], rig.Skeleton, wb);
            for (var b = 0; b < rig.Skeleton.Count; b++)
            {
                var d = Vector3.Distance(wa[b].Pos, wb[b].Pos);
                if (d > worst) { worst = d; worstBone = rig.Skeleton[b].Name; }
            }
        }
        _out.WriteLine($"worst {worst} in at {worstBone}");
        Assert.True(worst < 1e-3f, $"{worstBone} differs by {worst} in");
    }

    [Fact]
    public void SnakeCannotBeMirroredAndSaysWhy()
    {
        var rig = Rig("snake");
        Assert.Null(ClipCleanup.MirrorFrames(Walk(rig).Frames, rig, out var error));
        Assert.Contains("left and right", error);
    }

    [Theory]
    [InlineData("bird")]
    [InlineData("dog")]
    [InlineData("snake")]
    [InlineData("dragon")]
    public async Task GeneratesOnCreatures(string creature)
    {
        if (!UniMateSamplerTests.Available) return;
        var rig = Rig(creature);
        var generator = new UniMateGenerator(UniMateSamplerTests.Model());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = await generator.GenerateAsync(rig, new GenerationRequest
        {
            Mode = GenerationMode.TextToMotion, Prompts = new[] { creature == "bird" ? "flap its wings" : "walk forward" },
            DurationSeconds = 2f, OutputFps = 30f, Seed = 5, Steps = 4,
        }, null, default);
        _out.WriteLine($"{creature}: text to motion in {watch.ElapsedMilliseconds} ms");
        var motion = results.Single();
        AssertValid(rig, motion.Frames);

        // the encode path (in-between, edit with locks, variation) on the same rig
        var source = Walk(rig);
        var lockBones = rig.Analysis.Limbs.Where(l => l.Kind is LimbKind.Leg).SelectMany(l => l.Chain).ToArray();
        foreach (var mode in new[] { GenerationMode.InBetween, GenerationMode.TextEdit, GenerationMode.Variation })
        {
            var r = await generator.GenerateAsync(rig, new GenerationRequest
            {
                Mode = mode, Prompts = new[] { "turn around" }, OutputFps = 30f, Seed = 9, Steps = 3,
                SourceFrames = source.Frames, SourceFps = 30f, KeepFrames = new[] { 0, 60 }, KeepBones = lockBones,
            }, null, default);
            var frames = r.Single().Frames;
            AssertValid(rig, frames);
            Assert.Equal(source.FrameCount, frames.Count);
            if (mode == GenerationMode.TextEdit)
                foreach (var b in lockBones) Assert.Equal(source.Frames[30][b], frames[30][b]);
        }
    }

    static void AssertValid(MotionRig rig, List<XForm[]> frames)
    {
        Assert.InRange(frames.Count, 59, 62);
        foreach (var frame in frames)
        {
            Assert.Equal(rig.Skeleton.Count, frame.Length);
            foreach (var x in frame)
                Assert.True(float.IsFinite(x.Pos.X + x.Pos.Y + x.Pos.Z + x.Rot.X + x.Rot.Y + x.Rot.Z + x.Rot.W) && MathF.Abs(x.Rot.Length() - 1f) < 1e-3f);
        }
        // bones keep their lengths: only the root joint moves by translation
        for (var b = 0; b < rig.Skeleton.Count; b++)
        {
            if (b == rig.Analysis.BodyRoot || rig.Skeleton[b].ParentIndex < 0) continue;
            Assert.True(Vector3.Distance(frames[^1][b].Pos, rig.Skeleton[b].RestLocal.Pos) < 1e-3f, $"{rig.Skeleton[b].Name}: {frames[^1][b].Pos} vs rest {rig.Skeleton[b].RestLocal.Pos} (root {rig.Skeleton[rig.RootIndex].Name}, body root {rig.Skeleton[rig.Analysis.BodyRoot].Name})");
        }
    }
}

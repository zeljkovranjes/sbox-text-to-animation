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

    /// <summary>
    /// Every creature goes through upstream UniMate's preparation: real bones only (no invented joints), upstream's
    /// clean names and facing rule, canonical rest facing +Z with Y up.
    /// </summary>
    [Theory]
    [MemberData(nameof(CreatureNames))]
    public void BuildsAUniMateRigForEveryCreature(string creature)
    {
        var rig = Rig(creature);
        var u = UniMateRig.Build(rig);
        var s = rig.Skeleton;
        _out.WriteLine($"{creature}: {u.Count} joints, family {u.Family}, facing {u.Skeleton.RightHip}/{u.Skeleton.LeftHip} ({u.Prep.FaceSource}) body axis {u.Skeleton.BodyAxis}");
        _out.WriteLine(string.Join(", ", u.Skeleton.CleanNames));
        Assert.True(u.Count >= UniMateRig.MinJoints);
        Assert.All(u.Bone, b => Assert.InRange(b, 0, s.Count - 1));
        Assert.Equal(u.Count, u.Bone.Distinct().Count());
        Assert.All(u.Skeleton.CleanNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        for (var j = 0; j < u.Count; j++)
            Assert.Equal(UniMateNames.Clean(s[u.Bone[j]].Name, ""), u.Skeleton.CleanNames[j]);
        // the facing joints are exactly the ones upstream's rule picks from those names
        var kept = u.Prep.Kept;
        var (r, l, bodyAxis, _) = UniMateNames.ResolveFaceJoints(u.Prep.CleanNames, u.Prep.RawNames);
        Assert.Equal(r < 0 ? -1 : kept[r], u.Skeleton.RightHip < 0 ? -1 : u.Bone[u.Skeleton.RightHip]);
        Assert.Equal(l < 0 ? -1 : kept[l], u.Skeleton.LeftHip < 0 ? -1 : u.Bone[u.Skeleton.LeftHip]);
        Assert.Equal(bodyAxis, u.Skeleton.BodyAxis);
        if (u.Skeleton.RightHip < 0) return;
        // the canonical rest faces +Z with Y up: the face pair lies across X (or the body along Z)
        var across = u.Skeleton.TPose[u.Skeleton.RightHip] - u.Skeleton.TPose[u.Skeleton.LeftHip];
        if (u.Skeleton.BodyAxis) Assert.True(across.Z > 0.9f * across.Length(), $"head-tail {across}");
        else Assert.True(across.X < -0.9f * across.Length(), $"right-left {across}");
    }

    /// <summary>The bone order in the file doesn't change the result (only names and the hierarchy do, as upstream).</summary>
    [Theory]
    [MemberData(nameof(CreatureNames))]
    public void UniMateRigDoesNotDependOnBoneOrder(string creature)
    {
        var spec = RigAnalysisTests.SpecOf(creature);
        var a = MotionRig.Create(Creatures.Build(spec));
        var b = MotionRig.Create(Creatures.Build(spec, x => x, 31));
        var ua = UniMateRig.Build(a); var ub = UniMateRig.Build(b);
        Assert.Equal(ua.Count, ub.Count);
        string NameA(int j) => a.Skeleton[ua.Bone[j]].Name;
        string NameB(int j) => b.Skeleton[ub.Bone[j]].Name;
        Assert.Equal(Enumerable.Range(0, ua.Count).Select(NameA).OrderBy(n => n), Enumerable.Range(0, ub.Count).Select(NameB).OrderBy(n => n));
        Assert.Equal(ua.Skeleton.RightHip < 0 ? "" : NameA(ua.Skeleton.RightHip), ub.Skeleton.RightHip < 0 ? "" : NameB(ub.Skeleton.RightHip));
        Assert.Equal(ua.Skeleton.LeftHip < 0 ? "" : NameA(ua.Skeleton.LeftHip), ub.Skeleton.LeftHip < 0 ? "" : NameB(ub.Skeleton.LeftHip));
        for (var j = 0; j < ua.Count; j++)
        {
            var k = Enumerable.Range(0, ub.Count).First(i => NameB(i) == NameA(j));
            Assert.Equal(ua.Skeleton.CleanNames[j], ub.Skeleton.CleanNames[k]);
            Assert.True(Vector3.Distance(ua.Skeleton.TPose[j], ub.Skeleton.TPose[k]) < 1e-5f, $"{NameA(j)}");
        }
    }

    [Fact]
    public void RenamedHumanIsRecognisedFromItsShape()
    {
        var (skeleton, rename) = RigAnalysisTests.RenamedHuman();
        var renamed = MotionRig.Create(skeleton);
        Assert.True(renamed.IsHumanoid);
        Assert.Equal(2, renamed.Feet.Count);
        Assert.True(Vector3.Dot(renamed.Forward, Vector3.UnitX) > 0.99f);
        Assert.Equal(rename("pelvis"), renamed.Skeleton[renamed.HipsIndex].Name);
    }

    /// <summary>Rigs bigger than any training skeleton run whole: the network has no per-joint limit.</summary>
    [Fact]
    public void LongRigsKeepEveryBone()
    {
        var rig = Rig("dragon");
        Assert.True(rig.Skeleton.Count > UniMateRig.TrainedMaxJoints);
        var u = UniMateRig.Build(rig);
        Assert.Equal(rig.Skeleton.Count, u.Count); // no skin data attached: nothing is pruned
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

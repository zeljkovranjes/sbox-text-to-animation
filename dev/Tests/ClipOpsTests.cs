using System;
using System.Linq;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using Xunit;

namespace TextToAnimation.Tests;

public class ClipOpsTests
{
    readonly MotionRig _rig = Fixtures.HumanRig();

    [Fact]
    public void RigIsRecognisedAsHumanoidInEngineSpace()
    {
        Assert.True(_rig.IsHumanoid, string.Join("; ", _rig.Problems));
        Assert.Equal("pelvis", _rig.Skeleton[_rig.HipsIndex].Name);
        Assert.Equal(Vector3.UnitZ, _rig.Up);
        Assert.True(_rig.HipHeight > 30f && _rig.HipHeight < 45f, $"hip height {_rig.HipHeight} in");
        Assert.True(Vector3.Dot(_rig.Forward, Vector3.UnitX) > 0.9f, $"forward {_rig.Forward}");
        Assert.Equal(BodyRegion.ArmL, _rig.RegionOf(_rig.Skeleton.IndexOf("arm_lower_L")));
        Assert.Equal(BodyRegion.HandR, _rig.RegionOf(_rig.Skeleton.IndexOf("finger_index_1_R")));
        Assert.False(_rig.IsMotionBone(_rig.Skeleton.IndexOf("root_IK")));
    }

    [Fact]
    public void CropKeepsRangeAndRemapsAnnotations()
    {
        var clip = Fixtures.Walk(_rig);
        clip.PinnedFrames.Add(5); clip.PinnedFrames.Add(30);
        clip.Events.Add(new ClipEvent { EventClass = "X", Frame = 40 });
        var expected = clip.Frames[20][_rig.HipsIndex];
        ClipOps.Crop(clip, _rig, 20, 50);
        Assert.Equal(31, clip.FrameCount);
        Assert.Equal(expected, clip.Frames[0][_rig.HipsIndex]);
        Assert.Equal(new[] { 10 }, clip.PinnedFrames.ToArray());
        Assert.Equal(20, clip.Events.Single().Frame);
    }

    [Fact]
    public void DeleteSectionKeepsRootTravelContinuous()
    {
        var clip = Fixtures.Walk(_rig);
        ClipOps.DeleteSection(clip, _rig, 20, 39);
        Assert.Equal(41, clip.FrameCount);
        AssertContinuousPath(clip);
    }

    [Fact]
    public void DuplicateSectionContinuesThePathForward()
    {
        var clip = Fixtures.Walk(_rig);
        var before = RootTools.RootWorld(clip.Frames, _rig, clip.FrameCount - 1).Pos;
        ClipOps.DuplicateSection(clip, _rig, 10, 29);
        Assert.Equal(81, clip.FrameCount);
        AssertContinuousPath(clip);
        var after = RootTools.RootWorld(clip.Frames, _rig, clip.FrameCount - 1).Pos;
        Assert.True(Vector3.Dot(after - before, _rig.Forward) > 20f, "the duplicate should add forward travel");
    }

    [Fact]
    public void ReverseTwiceIsIdentity()
    {
        var clip = Fixtures.Walk(_rig);
        var original = AnimClip.CopyFrames(clip.Frames);
        ClipOps.Reverse(clip, _rig);
        ClipOps.Reverse(clip, _rig);
        for (var f = 0; f < original.Count; f++)
            Assert.True(Fixtures.MaxRotationDifference(original[f], clip.Frames[f]) < 0.01f);
    }

    [Fact]
    public void ResampleKeepsDurationAndTimeScaleChangesIt()
    {
        var clip = Fixtures.Walk(_rig);
        ClipOps.Resample(clip, _rig, 60f);
        Assert.Equal(121, clip.FrameCount);
        Assert.Equal(2f, clip.Duration, 3);
        ClipOps.TimeScale(clip, _rig, 2f);
        Assert.Equal(1f, clip.Duration, 2);
    }

    [Fact]
    public void SplitProducesTwoClipsSharingTheCutFrame()
    {
        var clip = Fixtures.Walk(_rig);
        var cut = clip.Frames[25][_rig.HipsIndex];
        var second = ClipOps.Split(clip, _rig, 25, "walk_b");
        Assert.Equal(26, clip.FrameCount);
        Assert.Equal(36, second.FrameCount);
        Assert.Equal(cut, second.Frames[0][_rig.HipsIndex]);
        Assert.NotEqual(clip.Id, second.Id);
    }

    [Fact]
    public void MakeInPlaceRemovesHorizontalTravel()
    {
        var clip = Fixtures.Walk(_rig);
        ClipOps.MakeInPlace(clip, _rig);
        var path = RootTools.HipsTrajectory(clip.Frames, _rig);
        var travel = RootTools.Horizontal(_rig, path[^1] - path[0]).Length();
        Assert.True(travel < 1f, $"in-place clip still travels {travel} in");
    }

    [Fact]
    public void RemoveRootDriftMakesEndMatchStart()
    {
        var clip = Fixtures.Walk(_rig, speedCmPerSec: 10f, turnDegPerSec: 20f);
        ClipOps.RemoveRootDrift(clip, _rig);
        var a = RootTools.GroundFrame(clip.Frames, _rig, 0);
        var b = RootTools.GroundFrame(clip.Frames, _rig, clip.FrameCount - 1);
        Assert.True((a.Pos - b.Pos).Length() < 0.05f, $"drift left {(a.Pos - b.Pos).Length()}");
        Assert.True(MathQ.AngleBetween(a.Rot, b.Rot) < 0.01f);
    }

    [Fact]
    public void SeamlessLoopClosesTheSeamButKeepsTravel()
    {
        var clip = Fixtures.Walk(_rig, frames: 50);
        var issuesBefore = ClipQuality.Analyze(new AnimClip { Frames = clip.Frames, Fps = 30, Looping = true }, _rig);
        Assert.Contains(issuesBefore, i => i.Code == "seam");
        var travelBefore = RootTools.Horizontal(_rig, RootTools.RootWorld(clip.Frames, _rig, clip.FrameCount - 1).Pos - RootTools.RootWorld(clip.Frames, _rig, 0).Pos).Length();
        ClipOps.MakeSeamlessLoop(clip, _rig, 10);
        Assert.True(clip.Looping);
        var worst = 0f;
        for (var b = 0; b < _rig.Skeleton.Count; b++)
            if (b != _rig.RootIndex && _rig.IsMotionBone(b))
                worst = MathF.Max(worst, MathQ.AngleBetween(clip.Frames[0][b].Rot, clip.Frames[^1][b].Rot));
        Assert.True(worst * 180f / MathF.PI < 0.5f, $"seam still {worst * 180f / MathF.PI}°");
        var travelAfter = RootTools.Horizontal(_rig, RootTools.RootWorld(clip.Frames, _rig, clip.FrameCount - 1).Pos - RootTools.RootWorld(clip.Frames, _rig, 0).Pos).Length();
        Assert.True(MathF.Abs(travelAfter - travelBefore) < 2f);
        Assert.DoesNotContain(ClipQuality.Analyze(clip, _rig), i => i.Code == "seam");
    }

    [Fact]
    public void OffsetRootTurnsTheWholePath()
    {
        var clip = Fixtures.Walk(_rig);
        ClipOps.OffsetRoot(clip, _rig, Vector3.Zero, 90f, progressive: false);
        var path = RootTools.Trajectory(clip.Frames, _rig);
        var dir = Vector3.Normalize(RootTools.Horizontal(_rig, path[^1] - path[0]));
        Assert.True(Vector3.Dot(dir, _rig.Lateral) > 0.95f, $"path dir {dir}");
    }

    [Fact]
    public void KeyLayerFadesInAndOut()
    {
        var clip = Fixtures.Walk(_rig);
        var head = _rig.Skeleton.IndexOf("head");
        var turn = new XForm(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f));
        clip.Keys.FalloffFrames = 5;
        clip.Keys.SetKey("head", 30, turn);
        var final = clip.EvaluateFrames(_rig.Skeleton);
        Assert.True(MathQ.AngleBetween(final[30][head].Rot, clip.Frames[30][head].Rot) > 0.99f);
        Assert.True(MathQ.AngleBetween(final[20][head].Rot, clip.Frames[20][head].Rot) < 1e-4f);
        var mid = MathQ.AngleBetween(final[28][head].Rot, clip.Frames[28][head].Rot);
        Assert.InRange(mid, 0.2f, 0.95f);
    }

    [Fact]
    public void UndoRedoRestoresState()
    {
        var clip = Fixtures.Walk(_rig);
        var undo = new UndoStack();
        undo.Record(clip, "Crop");
        ClipOps.Crop(clip, _rig, 10, 20);
        Assert.Equal(11, clip.FrameCount);
        Assert.Equal("Crop", undo.Undo(clip));
        Assert.Equal(61, clip.FrameCount);
        undo.Redo(clip);
        Assert.Equal(11, clip.FrameCount);
    }

    [Fact]
    public void QualityFlagsScaleAndUpsideDown()
    {
        var clip = Fixtures.Walk(_rig);
        Assert.DoesNotContain(ClipQuality.Analyze(clip, _rig), i => i.Severity == IssueSeverity.Error);
        var flip = clip.CloneDeep();
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        for (var f = 0; f < flip.FrameCount; f++)
        {
            var w = RootTools.RootWorld(flip.Frames, _rig, f);
            RootTools.SetRootWorld(flip.Frames, _rig, f, new XForm(Vector3.Transform(w.Pos, q), q * w.Rot));
        }
        Assert.Contains(ClipQuality.Analyze(flip, _rig), i => i.Code == "axes" && i.Severity == IssueSeverity.Error);
        var big = clip.CloneDeep();
        for (var f = 0; f < big.FrameCount; f++)
        {
            var w = RootTools.RootWorld(big.Frames, _rig, f);
            RootTools.SetRootWorld(big.Frames, _rig, f, new XForm(w.Pos + _rig.Up * 100f, w.Rot));
        }
        Assert.Contains(ClipQuality.Analyze(big, _rig), i => i.Code == "float");
        ClipCleanup.GroundFeet(big, _rig);
        Assert.DoesNotContain(ClipQuality.Analyze(big, _rig), i => i.Code is "float" or "sink");
    }

    void AssertContinuousPath(AnimClip clip)
    {
        var path = RootTools.Trajectory(clip.Frames, _rig);
        var typical = (path[1] - path[0]).Length();
        for (var f = 1; f < path.Length; f++)
        {
            var step = (path[f] - path[f - 1]).Length();
            Assert.True(step < typical * 3f + 0.5f, $"root jumps {step} in at frame {f} (typical {typical})");
        }
    }
}

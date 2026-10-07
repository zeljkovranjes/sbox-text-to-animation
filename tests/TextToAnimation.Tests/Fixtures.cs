using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Mapping;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Rig;

namespace TextToAnimation.Tests;

/// <summary>Shared test fixtures: the s&amp;box human skeleton in ENGINE space (inches, Z-up) and procedural motion.</summary>
public static class Fixtures
{
    static Skeleton _human;

    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>
    /// The s&amp;box human male skeleton as the engine reports it (Model.Bones): the shipped rig JSON is cm Y-up,
    /// so positions are scaled to inches and parentless bones rotated +90° about X (Y-up to Z-up).
    /// </summary>
    public static Skeleton HumanEngine()
    {
        if (_human is not null) return _human;
        var rig = TargetRig.Load(File.ReadAllText(Path("target_rig_sbox.json")));
        // Y-up -> Z-up, then the compiler's +90° yaw so the character faces +X like compiled s&box models
        var toZUp = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);
        var defs = new List<BoneDefinition>();
        foreach (var bone in rig.Skeleton.Bones)
        {
            var local = bone.RestLocal;
            var pos = local.Pos * MotionRig.EngineUnitsPerCm;
            var rot = local.Rot;
            if (bone.ParentIndex < 0) { pos = Vector3.Transform(pos, toZUp); rot = Quaternion.Normalize(toZUp * rot); }
            defs.Add(new BoneDefinition(bone.Name, bone.ParentIndex < 0 ? null : rig.Skeleton[bone.ParentIndex].Name, new XForm(pos, rot)));
        }
        return _human = Skeleton.Create(defs);
    }

    public static MotionRig HumanRig() => MotionRig.Create(HumanEngine());

    /// <summary>A procedural walk: forward travel, swinging legs and arms, bobbing hips.</summary>
    public static AnimClip Walk(MotionRig rig, int frames = 61, float fps = 30f, float speedCmPerSec = 120f, float turnDegPerSec = 0f)
    {
        var s = rig.Skeleton;
        var clip = new AnimClip { Name = "walk", Fps = fps, Origin = ClipOrigin.Generated };
        var restLocal = new XForm[s.Count];
        for (var i = 0; i < s.Count; i++) restLocal[i] = s[i].RestLocal;
        var legL = rig.Bone(BoneRole.UpperLegL).Value; var legR = rig.Bone(BoneRole.UpperLegR).Value;
        var kneeL = rig.Bone(BoneRole.LowerLegL).Value; var kneeR = rig.Bone(BoneRole.LowerLegR).Value;
        var armL = rig.Bone(BoneRole.UpperArmL).Value; var armR = rig.Bone(BoneRole.UpperArmR).Value;
        for (var f = 0; f < frames; f++)
        {
            var t = f / fps;
            var phase = t * MathF.PI * 2f * 0.9f;
            var frame = (XForm[])restLocal.Clone();
            // legs/arms swing about the character lateral axis, expressed in each bone's parent space
            Swing(rig, frame, legL, MathF.Sin(phase) * 0.45f);
            Swing(rig, frame, legR, -MathF.Sin(phase) * 0.45f);
            Swing(rig, frame, kneeL, -MathF.Max(0f, MathF.Sin(phase + 1.2f)) * 0.7f);
            Swing(rig, frame, kneeR, -MathF.Max(0f, -MathF.Sin(phase + 1.2f)) * 0.7f);
            Swing(rig, frame, armL, -MathF.Sin(phase) * 0.35f);
            Swing(rig, frame, armR, MathF.Sin(phase) * 0.35f);
            // root travel + bob + turn
            var yaw = Quaternion.CreateFromAxisAngle(rig.Up, turnDegPerSec * t * MathF.PI / 180f);
            var root = frame[rig.RootIndex];
            var travel = Vector3.Transform(rig.Forward * rig.Cm(speedCmPerSec) * t, yaw);
            frame[rig.RootIndex] = new XForm(root.Pos + travel + rig.Up * MathF.Sin(phase * 2f) * rig.Cm(2f), Quaternion.Normalize(yaw * root.Rot));
            clip.Frames.Add(frame);
        }
        ClipOps.Finish(clip, rig);
        clip.Revision = 0;
        return clip;
    }

    static void Swing(MotionRig rig, XForm[] frame, int bone, float radians)
    {
        // rotate about the character lateral axis in world space, converted to the bone's parent space
        var parent = rig.Skeleton[bone].ParentIndex;
        var parentWorld = TextToAnimation.Core.Processing.FkUtil.BoneWorld(frame, rig.Skeleton, parent);
        var axis = Vector3.Transform(rig.Lateral, Quaternion.Conjugate(parentWorld.Rot));
        frame[bone] = new XForm(frame[bone].Pos, Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, radians) * frame[bone].Rot));
    }

    public static float MaxRotationDifference(XForm[] a, XForm[] b)
    {
        var worst = 0f;
        for (var i = 0; i < a.Length; i++) worst = MathF.Max(worst, MathQ.AngleBetween(a[i].Rot, b[i].Rot));
        return worst * 180f / MathF.PI;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Formats.Fbx;
using TextToAnimation.Core.Maths;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// FBX export read back: the written file is parsed and every bone evaluated on every frame (its pre/post-rotation,
/// pivots, rotation order and curves, as an importer does); the poses must be the clip's.
/// </summary>
public class FbxClipExportTests
{
    readonly ITestOutputHelper _out;
    public FbxClipExportTests(ITestOutputHelper o) => _out = o;

    static List<FbxClipExport.Bone> Bones(TextToAnimation.Core.Rig.Skeleton s) => s.Bones.Select(b => new FbxClipExport.Bone(b.Name, b.ParentIndex, b.RestLocal)).ToList();

    /// <summary>Every model's world matrix (row-vector, file units and axes) at <paramref name="ticks"/> of the file's only stack.</summary>
    static Dictionary<string, Matrix4x4> Evaluate(FbxScene scene, long ticks)
    {
        var stack = scene.Stacks.Single();
        var models = scene.Models.ToHashSet();
        var world = new Dictionary<long, Matrix4x4>();
        Matrix4x4 W(FbxObject m)
        {
            if (world.TryGetValue(m.Id, out var w)) return w;
            var t = FbxTransform.FromModel(scene, m);
            Vector3 Sample(string property, Vector3 rest) => stack.Bindings.TryGetValue((m.Id, property), out var node)
                ? new Vector3(node.Component('X', ticks, rest.X), node.Component('Y', ticks, rest.Y), node.Component('Z', ticks, rest.Z)) : rest;
            var local = t.LocalMatrix(Sample("Lcl Translation", t.LclTranslation), Sample("Lcl Rotation", t.LclRotationDeg), Sample("Lcl Scaling", t.LclScaling));
            w = local * (m.ModelParent is { } p && models.Contains(p) ? W(p) : Matrix4x4.Identity);
            return world[m.Id] = w;
        }
        var result = new Dictionary<string, Matrix4x4>();
        foreach (var m in scene.Models) result[m.Name] = W(m);
        return result;
    }

    static XForm[] Worlds(TextToAnimation.Core.Rig.Skeleton s, XForm[] locals)
    {
        var w = new XForm[s.Count];
        for (var b = 0; b < s.Count; b++) w[b] = s[b].ParentIndex < 0 ? locals[b] : XForm.Compose(w[s[b].ParentIndex], locals[b]);
        return w;
    }

    /// <summary>Any rig's motion: every bone bent about its own axes, the root carried forward and turned.</summary>
    static List<XForm[]> Motion(TextToAnimation.Core.Rig.Skeleton s, int count = 40)
    {
        var frames = new List<XForm[]>();
        for (var t = 0; t < count; t++)
        {
            var pose = s.Bones.Select(b => b.RestLocal).ToArray();
            for (var b = 0; b < pose.Length; b++)
            {
                var axis = Vector3.Normalize(new Vector3(MathF.Sin(b * 1.7f + t * 0.3f), MathF.Cos(b * 0.9f), MathF.Sin(b * 2.3f + 0.5f)));
                pose[b].Rot = MathQ.Normalize(pose[b].Rot * Quaternion.CreateFromAxisAngle(axis, 0.4f * MathF.Sin(t * 0.25f + b)));
            }
            var root = s.Bones.ToList().FindIndex(b => b.ParentIndex < 0);
            pose[root].Pos += new Vector3(t * 0.8f, MathF.Sin(t * 0.2f) * 2, 0);
            pose[root].Rot = MathQ.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, t * 0.05f) * pose[root].Rot);
            frames.Add(pose);
        }
        return frames;
    }

    static long Ticks(int frame, float fps) => (long)Math.Round(frame * (double)FbxAnimCurve.TicksPerSecond / fps);

    [Fact]
    public void SkeletonExportPlaysTheClip()
    {
        var s = Fixtures.HumanRig().Skeleton;
        var clip = new AnimClip { Frames = Motion(s), Fps = 30 };
        var bytes = FbxClipExport.WriteSkeleton(Bones(s), clip.Frames, clip.Fps, "walk");
        var scene = FbxScene.Build(FbxTokenizer.Parse(bytes));
        Assert.Equal(2, scene.UpAxis);
        Assert.Equal(FbxClipExport.InchInCentimeters, scene.UnitScaleFactor, 6);
        float worstPos = 0, worstRot = 0; var where = "";
        for (var f = 0; f < clip.Frames.Count; f++)
        {
            var file = Evaluate(scene, Ticks(f, clip.Fps));
            var engine = Worlds(s, clip.Frames[f]);
            for (var b = 0; b < s.Count; b++)
            {
                var x = FbxTransform.ToRigid(file[s[b].Name]);
                var rot = MathQ.AngleBetween(x.Rot, engine[b].Rot) * 180 / MathF.PI;
                if (rot > worstRot) where = $"{s[b].Name} frame {f}";
                worstPos = MathF.Max(worstPos, (x.Pos - engine[b].Pos).Length());
                worstRot = MathF.Max(worstRot, rot);
            }
        }
        _out.WriteLine($"worst at {where}");
        _out.WriteLine($"{s.Count} bones x {clip.Frames.Count} frames: positions within {worstPos:0.0000} in, rotations within {worstRot:0.000} deg");
        Assert.True(worstPos < 0.01f && worstRot < 0.05f, $"pos {worstPos} rot {worstRot}");
    }

    public static IEnumerable<object[]> Sources()
    {
        var addon = @"C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\citizen\Assets\models\citizen_human\bodies\male\citizen_human_body_male.fbx";
        // a user's own model (not in the repository): checked when present on this machine
        var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "S&box Projects", "testing_zone", "Assets", "models");
        var gator = Path.Combine(models, "alligator_demo", "alligator_demo.fbx");
        if (File.Exists(addon)) yield return new object[] { addon, "citizen_human_engine_skeleton.json" };
        if (File.Exists(gator)) yield return new object[] { gator, "engine_skeleton_user_alligator.json" };
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void ModelExportPlaysTheClipOnTheSourceFile(string fbx, string engineSkeleton)
    {
        var s = EngineSkeletonTests.LoadEngineSkeleton(engineSkeleton);
        var clip = new AnimClip { Frames = Motion(s), Fps = 30 };
        var source = File.ReadAllBytes(fbx);
        var bytes = FbxClipExport.WriteIntoSource(source, Bones(s), clip.Frames, clip.Fps, "walk", out var report);
        _out.WriteLine(report);
        // the file keeps everything but its animation: the same models, geometry, deformers, materials
        static List<string> NonAnimation(byte[] data) => FbxTokenizer.Parse(data).Children.First(n => n.Name == "Objects").Children
            .Where(n => !n.Name.StartsWith("Animation", StringComparison.Ordinal)).Select(n => n.Name).ToList();
        Assert.Equal(NonAnimation(source), NonAnimation(bytes));
        var scene = FbxScene.Build(FbxTokenizer.Parse(bytes));
        Assert.Single(scene.Stacks);

        // the file's bones must move as the engine's: travel since frame 0 scaled by the two bodies' size ratio, and
        // the rotation change since frame 0 (an angle, so independent of the axes the import changed)
        // bones the file records a bind pose for (end bones without one may sit elsewhere than the engine put them,
        // so they travel differently while turning exactly as the engine's do)
        var bound = scene.Models.Where(m => scene.SkinBind.ContainsKey(m.Id) || scene.BindPose.ContainsKey(m.Id)).Select(m => m.Name).ToHashSet();
        var names = s.Bones.Select(b => b.Name).Where(n => scene.Models.Any(m => m.Name == n) && (bound.Count < 3 || bound.Contains(n))).ToList();
        var index = s.Bones.Select((b, i) => (b.Name, i)).ToDictionary(x => x.Name, x => x.i);
        var file0 = Evaluate(scene, 0);
        var engine0 = Worlds(s, clip.Frames[0]);
        var ratio = Spread(names.Select(n => FbxTransform.ToRigid(file0[n]).Pos)) / Spread(names.Select(n => engine0[index[n]].Pos));
        float worstPos = 0, worstRot = 0, travel = 0;
        for (var f = 0; f < clip.Frames.Count; f += 3)
        {
            var file = Evaluate(scene, Ticks(f, clip.Fps));
            var engine = Worlds(s, clip.Frames[f]);
            foreach (var n in names)
            {
                var b = index[n];
                var x = FbxTransform.ToRigid(file[n]); var x0 = FbxTransform.ToRigid(file0[n]);
                var dEngine = (engine[b].Pos - engine0[b].Pos).Length() * ratio;
                travel = MathF.Max(travel, dEngine);
                worstPos = MathF.Max(worstPos, MathF.Abs((x.Pos - x0.Pos).Length() - dEngine));
                var aFile = MathQ.AngleBetween(x.Rot, x0.Rot); var aEngine = MathQ.AngleBetween(engine[b].Rot, engine0[b].Rot);
                worstRot = MathF.Max(worstRot, MathF.Abs(aFile - aEngine) * 180 / MathF.PI);
            }
        }
        _out.WriteLine($"{Path.GetFileName(fbx)}: {names.Count} bones; travel differs by up to {worstPos:0.0000} (of {travel:0.00} file units), rotation change by up to {worstRot:0.000} deg");
        Assert.True(names.Count >= s.Count / 3, $"only {names.Count} bones found in the file");
        Assert.True(worstRot < 0.1f, $"rotation {worstRot}");
        Assert.True(worstPos < 0.005f * MathF.Max(1, travel), $"travel {worstPos}");
    }

    /// <summary>An FBX 6 source (transforms in Properties60) is refused with a clear message, not written wrong.</summary>
    [Fact]
    public void OldFbxSourcesAreRefused()
    {
        var croc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "S&box Projects", "testing_zone", "Assets", "models", "crocodile_2", "crocodile_2.fbx");
        if (!File.Exists(croc)) return;
        var source = File.ReadAllBytes(croc);
        Assert.False(FbxClipExport.CanWriteInto(source));
        var s = EngineSkeletonTests.LoadEngineSkeleton("engine_skeleton_user_crocodile.json");
        var e = Assert.Throws<NotSupportedException>(() => FbxClipExport.WriteIntoSource(source, Bones(s), Motion(s, 3), 30, "x", out _));
        _out.WriteLine(e.Message);
    }

    static float Spread(IEnumerable<Vector3> points)
    {
        var l = points.ToList(); var c = l.Aggregate(Vector3.Zero, (a, v) => a + v) / l.Count;
        return MathF.Sqrt(l.Average(v => (v - c).LengthSquared()));
    }
}

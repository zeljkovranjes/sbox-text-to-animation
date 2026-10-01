using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using TextToAnimation.Rig;

namespace TextToAnimation.Tests;

/// <summary>
/// Procedural non-humanoid skeletons in engine space (inches, Z up, facing +X, left +Y), each bone with an
/// arbitrary rest rotation (the analysis must only depend on joint positions), plus variants with gibberish
/// bone names and shuffled bone order to prove nothing depends on names or indices.
/// </summary>
public static class Creatures
{
    public sealed record Spec(string Name, string Parent, Vector3 Cm);

    const float Inch = MotionRig.EngineUnitsPerCm;

    static IEnumerable<Spec> Mirrored(string name, string parent, params (string Name, Vector3 Cm)[] chain)
    {
        foreach (var side in new[] { "L", "R" })
        {
            var p = parent.Contains("{s}") ? parent.Replace("{s}", side) : parent;
            foreach (var (n, cm) in chain)
            {
                var full = n.Replace("{s}", side);
                yield return new Spec(full, p, side == "L" ? cm : new Vector3(cm.X, -cm.Y, cm.Z));
                p = full;
            }
        }
    }

    static IEnumerable<Spec> Chain(string parent, params (string Name, Vector3 Cm)[] chain)
    {
        var p = parent;
        foreach (var (n, cm) in chain) { yield return new Spec(n, p, cm); p = n; }
    }

    static Vector3 V(float x, float y, float z) => new(x, y, z);

    public static List<Spec> Bird()
    {
        var s = new List<Spec> { new("root", null, V(0, 0, 0)), new("pelvis", "root", V(0, 0, 60)) };
        s.AddRange(Chain("pelvis", ("spine_a", V(8, 0, 64)), ("spine_b", V(16, 0, 68))));
        s.AddRange(Chain("spine_b", ("neck_1", V(22, 0, 78)), ("neck_2", V(26, 0, 90)), ("neck_3", V(28, 0, 102)), ("head", V(32, 0, 110)), ("beak", V(44, 0, 108))));
        s.AddRange(Chain("pelvis", ("tail_1", V(-12, 0, 62)), ("tail_2", V(-24, 0, 62))));
        s.AddRange(Mirrored("leg", "pelvis", ("thigh_{s}", V(0, 6, 58)), ("shin_{s}", V(4, 7, 35)), ("tarsus_{s}", V(2, 7, 6))));
        foreach (var side in new[] { "L", "R" })
        {
            var y = side == "L" ? 1f : -1f;
            s.Add(new Spec($"toe_a_{side}", $"tarsus_{side}", V(10, 9 * y, 0)));
            s.Add(new Spec($"toe_b_{side}", $"tarsus_{side}", V(11, 7 * y, 0)));
            s.Add(new Spec($"toe_c_{side}", $"tarsus_{side}", V(10, 5 * y, 0)));
            s.Add(new Spec($"toe_back_{side}", $"tarsus_{side}", V(-4, 7 * y, 1)));
        }
        s.AddRange(Mirrored("wing", "spine_b", ("wing_1_{s}", V(16, 10, 70)), ("wing_2_{s}", V(16, 40, 70)), ("wing_3_{s}", V(16, 70, 70)), ("wing_4_{s}", V(16, 95, 70))));
        return s;
    }

    public static List<Spec> Dog()
    {
        var s = new List<Spec> { new("root", null, V(-30, 0, 0)), new("pelvis", "root", V(-30, 0, 50)) };
        s.AddRange(Chain("pelvis", ("spine_1", V(-15, 0, 52)), ("spine_2", V(0, 0, 53)), ("spine_3", V(15, 0, 52))));
        s.AddRange(Chain("spine_3", ("neck_1", V(25, 0, 58)), ("neck_2", V(32, 0, 66)), ("head", V(38, 0, 70)), ("jaw", V(48, 0, 63))));
        s.AddRange(Mirrored("ear", "head", ("ear_{s}", V(36, 5, 78)), ("ear_tip_{s}", V(34, 7, 86))));
        s.AddRange(Chain("pelvis", ("tail_1", V(-38, 0, 52)), ("tail_2", V(-48, 0, 50)), ("tail_3", V(-58, 0, 46)), ("tail_4", V(-66, 0, 40))));
        s.AddRange(Mirrored("hind", "pelvis", ("hip_{s}", V(-30, 8, 48)), ("knee_{s}", V(-24, 9, 30)), ("hock_{s}", V(-34, 9, 14)), ("paw_{s}", V(-30, 9, 2)), ("toe_{s}", V(-24, 9, 0))));
        s.AddRange(Mirrored("front", "spine_3", ("scapula_{s}", V(15, 8, 48)), ("elbow_{s}", V(14, 9, 28)), ("wrist_{s}", V(16, 9, 10)), ("fpaw_{s}", V(18, 9, 2)), ("ftoe_{s}", V(22, 9, 0))));
        return s;
    }

    public static List<Spec> Snake()
    {
        var s = new List<Spec> { new("root", null, V(0, 0, 2)) };
        var parent = "root";
        for (var i = 1; i <= 20; i++)
        {
            s.Add(new Spec($"seg_{i:00}", parent, V(i * 10, 0, 2)));
            parent = $"seg_{i:00}";
        }
        s.Add(new Spec("head", parent, V(212, 0, 3)));
        s.Add(new Spec("jaw", "head", V(222, 0, 1)));
        return s;
    }

    public static List<Spec> Dinosaur()
    {
        var s = new List<Spec> { new("pelvis", null, V(0, 0, 100)) };
        s.AddRange(Chain("pelvis", ("spine_1", V(20, 0, 105)), ("chest", V(40, 0, 110))));
        s.AddRange(Chain("chest", ("neck", V(55, 0, 120)), ("head", V(75, 0, 125)), ("jaw", V(90, 0, 112))));
        s.AddRange(Chain("pelvis", ("tail_1", V(-20, 0, 100)), ("tail_2", V(-45, 0, 96)), ("tail_3", V(-70, 0, 90)), ("tail_4", V(-95, 0, 84)), ("tail_5", V(-120, 0, 78))));
        s.AddRange(Mirrored("leg", "pelvis", ("thigh_{s}", V(0, 15, 95)), ("shin_{s}", V(8, 17, 55)), ("foot_{s}", V(0, 17, 15)), ("toe_{s}", V(15, 17, 0))));
        s.AddRange(Mirrored("arm", "chest", ("arm_{s}", V(40, 10, 100)), ("forearm_{s}", V(45, 12, 88)), ("hand_{s}", V(50, 12, 78))));
        return s;
    }

    public static List<Spec> Alligator()
    {
        var s = new List<Spec> { new("pelvis", null, V(0, 0, 20)) };
        s.AddRange(Chain("pelvis", ("spine_1", V(20, 0, 21)), ("spine_2", V(40, 0, 21))));
        s.AddRange(Chain("spine_2", ("neck", V(55, 0, 22)), ("head", V(75, 0, 22)), ("snout", V(105, 0, 19))));
        s.AddRange(Chain("head", ("jaw", V(100, 0, 15))));
        var parent = "pelvis";
        for (var i = 1; i <= 6; i++) { s.Add(new Spec($"tail_{i}", parent, V(-15 * i, 0, 20 - i))); parent = $"tail_{i}"; }
        s.AddRange(Mirrored("hind", "pelvis", ("hip_{s}", V(0, 10, 18)), ("knee_{s}", V(5, 25, 15)), ("foot_{s}", V(0, 28, 2)), ("toe_{s}", V(8, 30, 0))));
        s.AddRange(Mirrored("front", "spine_2", ("shoulder_{s}", V(40, 10, 18)), ("elbow_{s}", V(45, 24, 14)), ("hand_{s}", V(42, 27, 2)), ("finger_{s}", V(50, 29, 0))));
        return s;
    }

    /// <summary>A long-tailed dragon with far more bones than UniMate's joint budget.</summary>
    public static List<Spec> Dragon()
    {
        var s = Dinosaur();
        var parent = "tail_5";
        for (var i = 6; i <= 60; i++) { s.Add(new Spec($"tail_{i}", parent, V(-120 - (i - 5) * 6, 0, 78 - (i - 5) * 0.5f))); parent = $"tail_{i}"; }
        foreach (var side in new[] { "L", "R" })
        {
            var y = side == "L" ? 1f : -1f;
            var p = $"chest";
            for (var i = 1; i <= 12; i++) { s.Add(new Spec($"wing_{i}_{side}", p, V(40, (8 + i * 12) * y, 115))); p = $"wing_{i}_{side}"; }
        }
        return s;
    }

    /// <summary>
    /// Builds the engine skeleton. <paramref name="rename"/> replaces every name; <paramref name="shuffleSeed"/>
    /// shuffles the bone order (0 = keep). Rest rotations are pseudo-random but identical across variants.
    /// </summary>
    public static Skeleton Build(List<Spec> specs, Func<string, string> rename = null, int shuffleSeed = 0)
    {
        rename ??= n => n;
        var world = new Dictionary<string, XForm>();
        foreach (var spec in specs)
        {
            var h = (uint)spec.Name.Aggregate(17, (acc, c) => acc * 31 + c);
            var axis = Vector3.Normalize(new Vector3((h % 7) - 3.1f, (h / 7 % 5) - 2.1f, (h / 35 % 3) - 0.9f));
            var rot = Quaternion.CreateFromAxisAngle(axis, (h % 360) * MathF.PI / 180f);
            world[spec.Name] = new XForm(spec.Cm * Inch, rot);
        }
        var defs = specs.Select(spec =>
        {
            var w = world[spec.Name];
            var local = spec.Parent is null ? w : XForm.ToLocal(world[spec.Parent], w);
            return new BoneDefinition(rename(spec.Name), spec.Parent is null ? null : rename(spec.Parent), local);
        }).ToList();
        if (shuffleSeed != 0)
        {
            var rng = new Random(shuffleSeed);
            defs = defs.OrderBy(_ => rng.Next()).ToList();
        }
        return Skeleton.Create(defs);
    }

    /// <summary>A gibberish renaming ("bone_017", "jnt_c_x", "Bip001 xyz", non-English) that carries no anatomy.</summary>
    public static Func<string, string> Gibberish(IEnumerable<string> names)
    {
        var list = names.ToList();
        var styles = new Func<int, string>[] { i => $"bone_{i:000}", i => $"jnt_c_x{i}", i => $"Bip001 xyz{i}", i => $"骨{i}", i => $"Joint{i:00}.R.L" };
        var map = new Dictionary<string, string>();
        var rng = new Random(1234);
        var ids = Enumerable.Range(1, list.Count).OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < list.Count; i++) map[list[i]] = styles[i % styles.Length](ids[i]);
        return n => map[n];
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Rig;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The joint names and facing the port gives UniMate's own skeletons (74 Truebones animals, 200 Objaverse rigs, as
/// ordinary engine rigs) against the names and facing UniMate trained them with (fixtures/upstream_names, exported
/// from the published UniML3D conditioning). The motion follows the names: with the dataset's names the port makes
/// UniMate's motion exactly (DatasetMotionTests), so every name that differs is a joint UniMate reads differently.
/// </summary>
public class NamingAgreementTests
{
    readonly ITestOutputHelper _out;
    public NamingAgreementTests(ITestOutputHelper o) => _out = o;

    public sealed record Entry(string dataset, string key, string object_type, string[] names, int[] parents, string[] clean, float[][] tpos, float[][] rot, JsonElement[] face);

    public static List<Entry> Load() => JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream_names", "dataset_skeletons.json")));

    public static MotionRig RigOf(Entry e)
    {
        var J = e.names.Length; var basis = UniMateSkeleton.EngineCanonicalBasis;
        var world = new TextToAnimation.Maths.XForm[J];
        for (var u = 0; u < J; u++)
        {
            var canon = new Vector3(e.tpos[u][0], e.tpos[u][1], e.tpos[u][2]);
            var q = e.rot[u];
            var rc = M3.FromQuaternion(Quaternion.Normalize(new Quaternion(q[1], q[2], q[3], q[0])));
            world[u] = new TextToAnimation.Maths.XForm(basis.Transposed * canon * 40f, Quaternion.Normalize((basis.Transposed * rc * basis).ToQuaternion()));
        }
        // dataset joint names can repeat; engine bones need unique names
        var unique = e.names.Select((n, i) => e.names.Take(i).Contains(n) ? $"{n}_{i}" : n).ToArray();
        var defs = Enumerable.Range(0, J).Select(u => new BoneDefinition(unique[u], e.parents[u] < 0 ? null : unique[e.parents[u]],
            e.parents[u] < 0 ? world[u] : TextToAnimation.Maths.XForm.ToLocal(world[e.parents[u]], world[u]))).ToList();
        var sk = Skeleton.Create(defs);
        UniMateSkin.Attach(sk, null, e.object_type.ToLowerInvariant());
        return MotionRig.Create(sk);
    }

    public static (int Same, int Total, bool FaceSame, string Error) Score(Entry e, out List<string> diffs) => Score(e, out diffs, out _);

    /// <summary>Names compared on the joints the port animates; joints it leaves to the engine by design (a person's
    /// fingers, under the people statistics) are counted in <paramref name="leftOut"/>, not as disagreements.</summary>
    public static (int Same, int Total, bool FaceSame, string Error) Score(Entry e, out List<string> diffs, out int leftOut)
    {
        diffs = new List<string>(); leftOut = 0;
        try
        {
            var rig = RigOf(e); var sk = rig.Skeleton;
            var u = UniMateRig.Build(rig);
            var unique = e.names.Select((n, i) => e.names.Take(i).Contains(n) ? $"{n}_{i}" : n).ToArray();
            var same = 0; var compared = 0;
            for (var k = 0; k < e.names.Length; k++)
            {
                var c = Array.FindIndex(u.Bone, b => sk[b].Name == unique[k]);
                if (c < 0 && u.AsMixamoBody) { leftOut++; continue; }
                compared++;
                var mine = c < 0 ? "(left out)" : u.Skeleton.CleanNames[c];
                if (mine == e.clean[k]) same++; else diffs.Add($"{e.names[k]}: \"{e.clean[k]}\" vs \"{mine}\"");
            }
            int r = e.face[0].GetInt32(), l = e.face[1].GetInt32();
            string Bone(int j) => j < 0 ? "-" : sk[u.Bone[j]].Name;
            var faceSame = (r < 0 ? u.Skeleton.RightHip < 0 : u.Skeleton.RightHip >= 0 && Bone(u.Skeleton.RightHip) == unique[r])
                        && (l < 0 ? u.Skeleton.LeftHip < 0 : u.Skeleton.LeftHip >= 0 && Bone(u.Skeleton.LeftHip) == unique[l]);
            return (same, compared, faceSame, null);
        }
        catch (Exception ex) { return (0, e.names.Length, false, ex.Message); }
    }

    [Fact]
    public void NamesAgreeWithUniMatesData()
    {
        var all = Load();
        foreach (var ds in new[] { "truebones", "objaverse" })
        {
            int same = 0, total = 0, faces = 0, n = 0, errors = 0, left = 0;
            var worst = new List<(string, double, List<string>)>();
            foreach (var e in all.Where(x => x.dataset == ds))
            {
                var s = Score(e, out var diffs, out var lo);
                n++; left += lo; same += s.Same; total += s.Total; if (s.FaceSame) faces++; if (s.Error is not null) errors++;
                worst.Add((e.key, (double)s.Same / s.Total, diffs));
            }
            _out.WriteLine($"{ds}: {n} skeletons, joint names agree {100.0 * same / total:0.0}% ({same}/{total}), facing agrees on {faces}/{n}, {errors} failed; {left} joints of people left to the engine by design");
            foreach (var (k, rate, diffs) in worst.OrderBy(w => w.Item2).Take(6))
                _out.WriteLine($"   {k}: {rate * 100:0}%  e.g. {string.Join("; ", diffs.Take(4))}");
            // measured 2026-10-02: 95.4% / 90.1%, facing 64 / 184 (rule stage alone: 91.1% / 87.0%, facing 65 / 178)
            var (minNames, minFaces) = ds == "truebones" ? (0.954, 64) : (0.90, 184);
            Assert.True((double)same / total >= minNames, $"{ds}: names agree on only {100.0 * same / total:0.0}%");
            Assert.True(faces >= minFaces, $"{ds}: facing agrees on only {faces}/{n}");
        }
    }
}

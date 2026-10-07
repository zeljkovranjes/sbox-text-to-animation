using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using TextToAnimation.EditorTools.Inference.Runtime;
using TextToAnimation.EditorTools.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>Validates the C# port of UniMate's conditioning and feature code against reference fixtures.</summary>
public class UniMateCoreTests
{
    readonly ITestOutputHelper _out;
    public UniMateCoreTests(ITestOutputHelper output) => _out = output;

    public sealed class Npz : IDisposable
    {
        readonly ZipArchive _zip;
        public Npz(string path) => _zip = ZipFile.OpenRead(path);
        public NumpyArray this[string key] => NumpyArray.Read(_zip, key + ".npy");
        public void Dispose() => _zip.Dispose();
    }

    public static Npz Open(string name) => new(Fixtures.Path("unimate/" + name));

    static Quaternion Wxyz(float[] v, int i) => new(v[i + 1], v[i + 2], v[i + 3], v[i]);

    /// <summary>The fixture rig (Mixamo core in s&amp;box engine space) as a UniMate skeleton.</summary>
    public static UniMateSkeleton FixtureSkeleton()
    {
        using var rig = Open("rig_engine_space.npz");
        var parents = rig["parents"].Values.Select(v => (int)v).ToArray();
        var pos = rig["rest_world_pos"].Values;
        var rot = rig["rest_world_rot"].Values;
        var n = parents.Length;
        var p = Enumerable.Range(0, n).Select(i => new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2])).ToArray();
        var r = Enumerable.Range(0, n).Select(i => Wxyz(rot, i * 4)).ToArray();
        // caller order = mixamo order; RightUpLeg = 18, LeftUpLeg = 14. Clean names come in BFS order: build
        // once to learn the order, then map them back to caller order.
        var placeholder = Enumerable.Range(0, n).Select(i => $"j{i}").ToArray();
        var probe = UniMateSkeleton.Build(placeholder, parents, p, r, 18, 14, Vector3.UnitX, UniMateSkeleton.EngineUpBasis);
        var bfsNames = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Fixtures.Path("unimate/strings.json"))).RootElement
            .GetProperty("v2_cond.npz:clean_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        var names = new string[n];
        for (var j = 0; j < n; j++) names[probe.SourceIndex[j]] = bfsNames[j];
        return UniMateSkeleton.Build(names, parents, p, r, 18, 14, Vector3.UnitX, UniMateSkeleton.EngineUpBasis);
    }

    [Fact]
    public void CanonicalRestMatchesReference()
    {
        var s = FixtureSkeleton();
        using var cond = Open("v2_cond.npz");
        var tpos = cond["tpos"].Values;
        var offsets = cond["offsets"].Values;
        var parents = cond["parents"].Values.Select(v => (int)v).ToArray();
        Assert.Equal(parents, s.Parents);
        var worst = 0f;
        for (var j = 0; j < s.Count; j++)
        {
            worst = MathF.Max(worst, (s.TPose[j] - new Vector3(tpos[j * 3], tpos[j * 3 + 1], tpos[j * 3 + 2])).Length());
            worst = MathF.Max(worst, (s.Offsets[j] - new Vector3(offsets[j * 3], offsets[j * 3 + 1], offsets[j * 3 + 2])).Length());
        }
        _out.WriteLine($"tpos error {worst}");
        Assert.True(worst < 1e-5f, $"tpos error {worst}");
        Assert.Equal(cond["scale"].Values[0], s.Scale, 4);

        var relArr = cond["cond_joint_relations"];
        var rel = relArr.Values;
        var dist = cond["cond_graph_dist"].Values;
        var stride = relArr.Shape[^1];
        var depth = cond["depth"].Values;
        for (var i = 0; i < s.Count; i++)
        {
            Assert.Equal((long)depth[i], s.Depths[i]);
            for (var j = 0; j < s.Count; j++)
            {
                Assert.Equal((long)rel[i * stride + j], s.Relations[i, j]);
                Assert.Equal((long)dist[i * stride + j], s.GraphDist[i, j]);
            }
        }
    }

    [Fact]
    public void SpectralFeaturesAreLaplacianEigenvectors()
    {
        var s = FixtureSkeleton();
        // every column must be a unit eigenvector of the normalised Laplacian with ascending eigenvalues
        var n = s.Count;
        var deg = new double[n];
        for (var j = 1; j < n; j++) { deg[j]++; deg[s.Parents[j]]++; }
        double prev = 0;
        for (var c = 0; c < 8; c++)
        {
            var v = Enumerable.Range(0, n).Select(r => (double)s.Spectral[r, c]).ToArray();
            var lv = new double[n];
            for (var i = 0; i < n; i++)
            {
                lv[i] = v[i];
                for (var j = 0; j < n; j++)
                    if (j != i && (s.Parents[j] == i || s.Parents[i] == j)) lv[i] -= v[j] / Math.Sqrt(deg[i] * deg[j]);
            }
            var lambda = lv.Zip(v, (a, b) => a * b).Sum();
            var residual = Math.Sqrt(lv.Select((x, i) => (x - lambda * v[i]) * (x - lambda * v[i])).Sum());
            Assert.True(residual < 1e-5, $"column {c} residual {residual}");
            Assert.True(lambda >= prev - 1e-6, "eigenvalues must ascend");
            Assert.True(lambda > 1e-6, "the trivial eigenvector must be skipped");
            prev = lambda;
        }
    }

    [Fact]
    public void DecodeAndMapToEngineMatchReference()
    {
        var s = FixtureSkeleton();
        using var f = Open("v2_t2m_euler50_cfg3.npz");
        var featArr = f["features"];
        int T = featArr.Shape[0], J = featArr.Shape[1];
        var feat = new float[T, J, 12];
        for (var t = 0; t < T; t++) for (var j = 0; j < J; j++) for (var c = 0; c < 12; c++) feat[t, j, c] = featArr.Values[(t * J + j) * 12 + c];
        var motion = UniMateFeatures.Decode(feat, s.Parents);
        var (_, fkPos) = UniMateFeatures.Fk(motion, s.Offsets, s.Parents);
        var refFk = f["fk_pos_canon"].Values;
        var fkErr = 0f;
        for (var t = 0; t < T; t++) for (var j = 0; j < J; j++)
            fkErr = MathF.Max(fkErr, (fkPos[t, j] - new Vector3(refFk[(t * J + j) * 3], refFk[(t * J + j) * 3 + 1], refFk[(t * J + j) * 3 + 2])).Length());
        _out.WriteLine($"fk error {fkErr}");
        Assert.True(fkErr < 1e-3f, $"fk error {fkErr}");

        var src = UniMateFeatures.ToSource(motion, s);
        var refLocal = f["engine_local_q"].Values;
        var refRoot = f["engine_root_pos"].Values;
        var rotErr = 0f; var rootErr = 0f;
        for (var t = 0; t < T; t++)
        {
            rootErr = MathF.Max(rootErr, (src.RootPos[t] - new Vector3(refRoot[t * 3], refRoot[t * 3 + 1], refRoot[t * 3 + 2])).Length());
            for (var j = 0; j < J; j++)
                rotErr = MathF.Max(rotErr, 1f - MathF.Abs(Quaternion.Dot(src.LocalRot[t, j], Wxyz(refLocal, (t * J + j) * 4))));
        }
        _out.WriteLine($"engine local rot 1-|dot| {rotErr}, root {rootErr}");
        Assert.True(rotErr < 1e-5f, $"rotation error {rotErr}");
        Assert.True(rootErr < 1e-3f, $"root error {rootErr} in");
    }

    [Fact]
    public void EncodeUserMotionMatchesReference()
    {
        var s = FixtureSkeleton();
        using var rig = Open("rig_engine_space.npz");
        var pos = rig["anim_world_pos"]; var rot = rig["anim_world_rot"];
        int T1 = pos.Shape[0], J = s.Count;
        var wp = new Vector3[T1, J]; var wr = new Quaternion[T1, J];
        for (var t = 0; t < T1; t++)
            for (var j = 0; j < J; j++)
            {
                var src = s.SourceIndex[j]; // BFS joint j comes from caller joint src
                var i = t * J + src;
                wp[t, j] = new Vector3(pos.Values[i * 3], pos.Values[i * 3 + 1], pos.Values[i * 3 + 2]);
                wr[t, j] = Wxyz(rot.Values, i * 4);
            }
        var (feat, align) = UniMateFeatures.Encode(wp, wr, s);
        using var ib = Open("v2_inbetween_keep0_last.npz");
        var reference = ib["user_features"];
        var worst = 0f;
        for (var t = 0; t < reference.Shape[0]; t++)
            for (var j = 0; j < J; j++)
                for (var c = 0; c < 12; c++)
                    worst = MathF.Max(worst, MathF.Abs(feat[t, j, c] - reference.Values[(t * J + j) * 12 + c]));
        _out.WriteLine($"encode error {worst}, ground {align.Ground} vs {ib["ground"].Values[0]}");
        Assert.True(worst < 1e-4f, $"feature error {worst}");
        Assert.Equal(ib["ground"].Values[0], align.Ground, 4);
    }
}

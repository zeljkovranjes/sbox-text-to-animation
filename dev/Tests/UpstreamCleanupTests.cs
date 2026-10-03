using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using TextToAnimation.Animation;
using TextToAnimation.Editor.Engine;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The port of UniMate's own clean-up (its Blender add-on: prompt joins, collisions, ground contact) against the
/// add-on's Python on the same input: a generated clip on a real rig, with collision capsules fitted to its mesh.
/// Inputs: T2A_CLEANUP_MAKE=1 writes them (needs the model and the rig's FBX); then
/// dev/tools/upstream_prep/make_cleanup_fixtures.py runs the add-on's backend on each and writes the expected output.
/// </summary>
public class UpstreamCleanupTests
{
    readonly ITestOutputHelper _out; public UpstreamCleanupTests(ITestOutputHelper o) => _out = o;
    static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream_cleanup");
    static string SourceDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "upstream_cleanup"));

    static float[][] Matrix(Quaternion q)
    {
        // column-vector rotation matrix (the add-on's), from a quaternion
        var m = Matrix4x4.CreateFromQuaternion(q); // row-vector: v' = v * m
        return new[] { new[] { m.M11, m.M21, m.M31 }, new[] { m.M12, m.M22, m.M32 }, new[] { m.M13, m.M23, m.M33 } };
    }

    static Quaternion FromMatrix(JsonElement e)
    {
        var r = e.EnumerateArray().Select(row => row.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
        var m = new Matrix4x4(r[0][0], r[1][0], r[2][0], 0, r[0][1], r[1][1], r[2][1], 0, r[0][2], r[1][2], r[2][2], 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    static Vector3 V(JsonElement e) { var a = e.EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new(a[0], a[1], a[2]); }

    [Theory]
    [InlineData("citizen", @"C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\citizen\Assets\models\citizen_human\bodies\male\citizen_human_body_male.fbx", "A person walks forward.|A person turns around.")]
    [InlineData("fox", @"D:\99-scratch\t2a-weird-rigs\fbx\fox\fox.fbx", "A fox walks forward.|A fox turns around.")]
    public async Task MakeInput(string rigName, string fbx, string prompts)
    {
        if (Environment.GetEnvironmentVariable("T2A_CLEANUP_MAKE") != "1" || !UniMateSamplerTests.Available || !File.Exists(fbx)) return;
        var sk = rigName == "citizen" ? TextToAnimation.Tests.Fixtures.HumanRig().Skeleton : EngineSkeletonTests.LoadEngineSkeleton($"engine_skeleton_{rigName}.json");
        var rig = MotionRig.Create(sk);
        var mesh = FbxSkin.MeshPoints(File.ReadAllBytes(fbx))!.Value;
        var names = sk.Bones.Select(b => b.Name).ToList();
        int Bone(string n) => names.IndexOf(n) is var i && i >= 0 ? i : names.IndexOf(TextToAnimation.Editor.Formats.Fbx.FbxClipExport.EngineName(n));
        var matched = mesh.BindPositions.Where(kv => Bone(kv.Key) >= 0).ToList();
        var (m, _, error) = TextToAnimation.Editor.Formats.Fbx.FbxClipExport.Similarity(matched.Select(kv => kv.Value).ToList(), matched.Select(kv => (Vector3)sk.RestWorld[Bone(kv.Key)].Pos).ToList());
        _out.WriteLine($"{rigName}: {matched.Count} bones fitted, rms {error:0.###}");
        var points = mesh.Points.Where(kv => Bone(kv.Key) >= 0).ToDictionary(kv => Bone(kv.Key), kv => (IReadOnlyList<Vector3>)kv.Value.Select(p => Vector3.Transform(p, m)).ToList());
        UniMateSkin.Attach(sk, null, rigName, null, points);

        // a raw two-step sequence (no clean-up): the joins and the clean-up are what is compared
        var steps = prompts.Split('|');
        var r = (await new UniMateGenerator(UniMateSamplerTests.Model()).GenerateAsync(rig, new GenerationRequest
        {
            Mode = GenerationMode.Expansion, Prompts = steps, OutputFps = 30f, Seed = 7, CleanUp = false,
        }, null, default)).Single();
        var uni = UniMateRig.Build(rig);
        var s = UniMateCleanup.ForRig(rig, uni);
        var motion = UniMateCleanup.Subset(UniMateCleanup.FromFrames(r.Frames, sk), s);
        var cursors = Enumerable.Range(1, steps.Length - 1).Select(w => 60 + (w - 1) * 50).ToList();
        var clips = new List<object>();
        var startFrame = 0;
        foreach (var c in cursors.Append(motion.Frames)) { clips.Add(new { start = startFrame + 1, end = c, references = Array.Empty<object>() }); startFrame = c; }
        var feet = s.Profiles.Select(p => p.Joint).ToHashSet();
        var height = s.Capsules.Where(c => feet.Contains(c.Joint)).Select(c => MathF.Min(c.A.Z, c.B.Z) - c.Radius).DefaultIfEmpty(0).Min();
        Directory.CreateDirectory(SourceDir);
        File.WriteAllText(Path.Combine(SourceDir, $"{rigName}_input.json"), JsonSerializer.Serialize(new
        {
            rig = rigName,
            parents = s.Parents, heads = s.Heads.Select(V), labels = s.Labels, bone_names = s.Names,
            rest_matrices = Enumerable.Range(0, s.Count).Select(j =>
            {
                var x = s.RestX[j]; var y = s.RestY[j]; var z = Vector3.Cross(x, y); var h = s.Heads[j];
                return new[] { new[] { x.X, y.X, z.X, h.X }, new[] { x.Y, y.Y, z.Y, h.Y }, new[] { x.Z, y.Z, z.Z, h.Z }, new[] { 0f, 0f, 0f, 1f } };
            }),
            collision_capsules = s.Capsules.Select(c => new { joint = c.Joint, a = V(c.A), b = V(c.B), radius = c.Radius }),
            foot_profiles = s.Profiles.Select(p => new { joint = p.Joint, parent = p.Parent, upper = p.Upper, leg_length = p.LegLength, stance_tilt = p.StanceTilt, swing_tilt = p.SwingTilt }),
            ground = new { normal = new[] { 0f, 0f, 1f }, height, triangles = Array.Empty<object>() },
            clips,
            positions = motion.Pos.Select(f => f.Select(V)),
            rotations = motion.Rot.Select(f => f.Select(Matrix)),
        }));
        _out.WriteLine($"{rigName}: {s.Capsules.Count} capsules, {s.Profiles.Count} feet ({string.Join(", ", s.Profiles.Select(p => names[p.Joint]))}), {motion.Frames} frames, joins at {string.Join(",", cursors)}");
    }

    public static IEnumerable<object[]> Fixtures() => Directory.Exists(Dir)
        ? Directory.GetFiles(Dir, "*_expected.json").Select(f => new object[] { Path.GetFileName(f).Replace("_expected.json", "") })
        : Array.Empty<object[]>();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void CleanupMatchesUniMatesAddon(string rigName)
    {
        var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, $"{rigName}_input.json"))).RootElement;
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, $"{rigName}_expected.json"))).RootElement;
        var parents = input.GetProperty("parents").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var heads = input.GetProperty("heads").EnumerateArray().Select(V).ToArray();
        var s = new UniMateCleanup.Skeleton
        {
            Parents = parents, Heads = heads,
            Labels = input.GetProperty("labels").EnumerateArray().Select(x => x.GetString()).ToArray(),
            Names = input.GetProperty("bone_names").EnumerateArray().Select(x => x.GetString()).ToArray(),
            RestY = input.GetProperty("rest_matrices").EnumerateArray().Select(m => { var r = m.EnumerateArray().Select(row => row.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray(); return new Vector3(r[0][1], r[1][1], r[2][1]); }).ToArray(),
            RestX = input.GetProperty("rest_matrices").EnumerateArray().Select(m => { var r = m.EnumerateArray().Select(row => row.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray(); return new Vector3(r[0][0], r[1][0], r[2][0]); }).ToArray(),
            Capsules = input.GetProperty("collision_capsules").EnumerateArray().Select(c => new UniMateCleanup.Capsule(c.GetProperty("joint").GetInt32(), V(c.GetProperty("a")), V(c.GetProperty("b")), c.GetProperty("radius").GetSingle())).ToList(),
            Profiles = input.GetProperty("foot_profiles").EnumerateArray().Select(p => new UniMateCleanup.FootProfile
            {
                Joint = p.GetProperty("joint").GetInt32(), Parent = p.GetProperty("parent").GetInt32(), Upper = p.GetProperty("upper").GetInt32(),
                LegLength = p.GetProperty("leg_length").GetSingle(), StanceTilt = p.GetProperty("stance_tilt").GetSingle(), SwingTilt = p.GetProperty("swing_tilt").GetSingle(),
            }).ToList(),
        };
        var pos = input.GetProperty("positions").EnumerateArray().Select(f => f.EnumerateArray().Select(V).ToArray()).ToArray();
        var rot = input.GetProperty("rotations").EnumerateArray().Select(f => f.EnumerateArray().Select(FromMatrix).ToArray()).ToArray();
        var motion = new UniMateCleanup.Motion
        {
            Pos = pos, Rot = rot,
            Offset = pos.Select(f => Enumerable.Range(0, parents.Length).Select(j => parents[j] < 0 ? Vector3.Zero : heads[j] - heads[parents[j]]).ToArray()).ToArray(),
        };
        var cursors = input.GetProperty("clips").EnumerateArray().Skip(1).Select(c => c.GetProperty("start").GetInt32() - 1).ToList();
        var stages = Environment.GetEnvironmentVariable("T2A_CLEANUP_STAGES");
        void Stage(string name)
        {
            if (string.IsNullOrEmpty(stages)) return;
            Directory.CreateDirectory(stages);
            File.WriteAllText(Path.Combine(stages, $"{rigName}_{name}.json"), JsonSerializer.Serialize(new { positions = motion.Pos.Select(f => f.Select(V)), rotations = motion.Rot.Select(f => f.Select(Matrix)) }));
        }
        UniMateCleanup.Joins(motion, s, cursors); Stage("joins");
        string report;
        if (!string.IsNullOrEmpty(stages))
        {
            var a = UniMateCleanup.Collisions(motion, s); Stage("collisions1");
            var b = UniMateCleanup.Plant(motion, s); Stage("plant");
            var c = UniMateCleanup.Collisions(motion, s); Stage("collisions2");
            report = $"{a}; {b}; {c}";
        }
        else report = UniMateCleanup.Clean(motion, s);
        _out.WriteLine(report);
        _out.WriteLine("add-on: " + expected.GetProperty("report").GetString());

        var ePos = expected.GetProperty("positions").EnumerateArray().Select(f => f.EnumerateArray().Select(V).ToArray()).ToArray();
        var eRot = expected.GetProperty("rotations").EnumerateArray().Select(f => f.EnumerateArray().Select(FromMatrix).ToArray()).ToArray();
        var size = heads.Max(h => (h - heads[0]).Length());
        float worstPos = 0, worstDeg = 0; int at = 0, worstJoint = 0, worstFrame = 0, total = 0, overOne = 0; double sumDeg = 0;
        var perJoint = new Dictionary<int, int>(); var firstFrame = new Dictionary<int, int>();
        for (var t = 0; t < ePos.Length; t++)
            for (var j = 0; j < parents.Length; j++)
            {
                var d = (motion.Pos[t][j] - ePos[t][j]).Length() / size;
                if (d > worstPos) { worstPos = d; at = t; }
                var dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(motion.Rot[t][j], eRot[t][j])));
                var deg = 2f * MathF.Acos(dot) * 180f / MathF.PI;
                if (deg > worstDeg) { worstDeg = deg; worstJoint = j; worstFrame = t; }
                total++; sumDeg += deg; if (deg > 1f) { overOne++; perJoint[j] = perJoint.GetValueOrDefault(j) + 1; firstFrame.TryAdd(j, t); }
            }
        _out.WriteLine($"{rigName}: worst position {worstPos:0.0000} of the rig's size (frame {at}), worst rotation {worstDeg:0.00} deg ({s.Names[worstJoint]}, frame {worstFrame}, {parents.Count(p => p == worstJoint)} children); {overOne} of {total} joint-frames over 1 deg");
        _out.WriteLine("over 1 deg: " + string.Join(", ", perJoint.OrderByDescending(kv => kv.Value).Select(kv => $"{s.Names[kv.Key]} x{kv.Value} from frame {firstFrame[kv.Key]}")));
        // against the add-on with exact rotations (make_cleanup_fixtures.py): its own matrices drift, and on a deep chain
        // (fingers) the drift diverges; the port keeps rotations exact
        var meanDeg = sumDeg / total;
        _out.WriteLine($"mean rotation difference {meanDeg:0.000} deg");
        Assert.True(worstPos < 0.001f && meanDeg < 0.05 && worstDeg < 1f, $"{rigName}: port differs from the add-on by {worstPos:0.0000} of size / mean {meanDeg:0.000} deg / worst {worstDeg:0.00} deg");
    }
}

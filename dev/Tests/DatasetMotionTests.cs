using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using TextToAnimation.Editor.Inference.Onnx;
using TextToAnimation.Editor.Inference.UniMate;
using TextToAnimation.Generation;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The port's motion against UniMate's own text-to-motion on skeletons from UniMate's own datasets (Mixamo,
/// Truebones, Objaverse - dev/tools/upstream_prep/make_dataset_fixtures.py): upstream decides everything as its
/// sampler does (the dataset's statistics, names, spectral features); the port gets an ordinary engine rig of the
/// same skeleton and decides for itself. Same noise and prompt, so the motions must agree: this is what catches a
/// wrong statistics choice (a person normalised as an Objaverse object floats above the floor).
/// </summary>
public class DatasetMotionTests
{
    readonly ITestOutputHelper _out;
    public DatasetMotionTests(ITestOutputHelper o) => _out = o;

    static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream_dataset");

    public static IEnumerable<object[]> Cases() => Directory.Exists(Dir)
        ? Directory.GetFiles(Dir, "ds_*.json").Select(f => new object[] { Path.GetFileNameWithoutExtension(f)[3..] })
        : Enumerable.Empty<object[]>();

    static RigFamily FamilyOf(string dataset) => dataset switch { "mixamo" => RigFamily.Humanoid, "truebones" => RigFamily.Animal, _ => RigFamily.Object };

    [Theory]
    [MemberData(nameof(Cases))]
    public void PortMakesUpstreamsMotion(string tag)
    {
        if (!UniMateSamplerTests.Available) return;
        var j = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, $"ds_{tag}.json"))).RootElement;
        using var z = new UniMateCoreTests.Npz(Path.Combine(Dir, $"ds_{tag}.npz"));
        var dataset = j.GetProperty("dataset").GetString();
        var names = j.GetProperty("joint_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        var upParents = j.GetProperty("parents").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var upClean = j.GetProperty("clean_joint_names").EnumerateArray().Select(e => e.GetString()).ToArray();
        var J = names.Length;
        const int T = UniMateModel.Frames;

        // ---- the skeleton as an engine rig: canonical (Y up, facing +Z, diameter 2) -> engine (Z up, +X forward), 40 units
        var basis = UniMateSkeleton.EngineCanonicalBasis; // engine -> canonical
        var tp = z["tpos"].Values; var tr = z["tpos_global_rot"].Values;
        var world = new TextToAnimation.Maths.XForm[J];
        for (var u = 0; u < J; u++)
        {
            var canon = new Vector3(tp[u * 3], tp[u * 3 + 1], tp[u * 3 + 2]);
            var rc = M3.FromQuaternion(new Quaternion(tr[u * 4 + 1], tr[u * 4 + 2], tr[u * 4 + 3], tr[u * 4]));
            var re = basis.Transposed * rc * basis;
            world[u] = new TextToAnimation.Maths.XForm(basis.Transposed * canon * 40f, Quaternion.Normalize(re.ToQuaternion()));
        }
        var defs = Enumerable.Range(0, J).Select(u => new TextToAnimation.Rig.BoneDefinition(names[u], upParents[u] < 0 ? null : names[upParents[u]],
            upParents[u] < 0 ? world[u] : TextToAnimation.Maths.XForm.ToLocal(world[upParents[u]], world[u]))).ToList();
        var skeleton = TextToAnimation.Rig.Skeleton.Create(defs);
        UniMateSkin.Attach(skeleton, null, j.GetProperty("object_type").GetString().ToLowerInvariant());
        var rig = TextToAnimation.Animation.MotionRig.Create(skeleton);

        // ---- the port decides: preparation, facing, names, statistics
        var uni = UniMateRig.Build(rig);
        Assert.Equal(J, uni.Count);
        var cOf = Enumerable.Range(0, J).Select(u => Array.FindIndex(uni.Bone, b => skeleton[b].Name == names[u])).ToArray();
        Assert.DoesNotContain(-1, cOf);
        var nameDiffs = Enumerable.Range(0, J).Count(u => uni.Skeleton.CleanNames[cOf[u]] != upClean[u]);
        var face = j.GetProperty("face");
        var faceSame = face.GetProperty("r").GetInt32() is var r && face.GetProperty("l").GetInt32() is var l
            && (r < 0 ? uni.Skeleton.RightHip < 0 : uni.Skeleton.RightHip == cOf[r]) && (l < 0 ? uni.Skeleton.LeftHip < 0 : uni.Skeleton.LeftHip == cOf[l]);
        var expected = FamilyOf(dataset);
        if (uni.Family != expected)
        {
            var an = rig.Analysis;
            _out.WriteLine($"  analysis: humanoid {an.IsHumanoid}, facing {an.Facing}, limbs [{string.Join("; ", an.Limbs.Select(x => x.Kind + ":" + string.Join(",", x.Chain.Select(b => skeleton[b].Name))))}], tails [{string.Join("; ", an.Tails.Select(t => string.Join(",", t.Select(b => skeleton[b].Name))))}]");
        }
        _out.WriteLine($"{tag}: {J} joints; statistics {uni.Family} (upstream {dataset}); {nameDiffs} clean names differ from the dataset's; facing {(faceSame ? "same" : "different")}");

        var model = UniMateSamplerTests.Model();
        var noiseUp = z["noise"].Values; var noise = new float[J * 12 * T];
        for (var u = 0; u < J; u++) Array.Copy(noiseUp, u * 12 * T, noise, cOf[u] * 12 * T, 12 * T);
        var caption = z["caption_emb"].Values;
        // upstream's text-to-motion sampler: dopri5 to convergence (the port's own); older references used fixed Euler
        var settings = j.TryGetProperty("steps", out var fixedSteps)
            ? new SampleSettings { Method = Integrator.Euler, Steps = fixedSteps.GetInt32(), Guidance = j.GetProperty("cfg").GetSingle() } // older references: fixed Euler
            : new SampleSettings { Method = Integrator.Dopri5, Guidance = j.GetProperty("cfg").GetSingle() };
        var fkUp = z["fk_pos"].Values;
        var parentsC = uni.Skeleton.Parents;

        (float Worst, float Mean, float GroundDiff) Compare(UniMateStats stats, PreparedSkeleton prepared)
        {
            var x = model.Sample(prepared, caption, noise, settings, null, default);
            var motion = UniMateFeatures.Decode(UniMateFeatures.FromModel(x, J, T, stats), parentsC);
            var (_, pos) = UniMateFeatures.Fk(motion, uni.Skeleton.Offsets, parentsC);
            float worst = 0, sum = 0, ground = 0;
            for (var t = 0; t < T; t++)
            {
                float lowC = float.MaxValue, lowU = float.MaxValue;
                for (var u = 0; u < J; u++)
                {
                    var up = new Vector3(fkUp[(t * J + u) * 3], fkUp[(t * J + u) * 3 + 1], fkUp[(t * J + u) * 3 + 2]);
                    var d = (pos[t, cOf[u]] - up).Length();
                    worst = MathF.Max(worst, d); sum += d;
                    lowC = MathF.Min(lowC, pos[t, cOf[u]].Y); lowU = MathF.Min(lowU, up.Y);
                }
                ground = MathF.Max(ground, MathF.Abs(lowC - lowU));
            }
            return (worst, sum / (T * J), ground);
        }

        // the port as it runs in the editor (its own names, spectral features and statistics)
        var ownStats = UniMateStats.For(uni.Family);
        var own = Compare(ownStats, model.Prepare(uni.Skeleton, ownStats, default));
        _out.WriteLine($"  port as is: joint positions mean {own.Mean:0.0000}, max {own.Worst:0.0000}; lowest point differs by up to {own.GroundDiff:0.0000} (canonical units, body diameter 2)");

        // the same with the dataset's own names and spectral features: what remains is arithmetic
        var spec = z["spectral"].Values; var sp = new float[J, 8];
        for (var u = 0; u < J; u++) for (var c = 0; c < 8; c++) sp[cOf[u], c] = spec[u * 8 + c];
        uni.Skeleton.SetSpectral(sp);
        var datasetStats = UniMateStats.For(expected);
        var inputs = model.ConditioningInputs(uni.Skeleton, datasetStats, default);
        var emb = z["name_emb"].Values; var width = emb.Length / J; var names2 = new float[J * width];
        for (var u = 0; u < J; u++) Array.Copy(emb, u * width, names2, cOf[u] * width, width);
        inputs["name_emb"] = Tensor.Float(new[] { J, width }, names2);
        var preparedExact = model.Prepare(uni.Skeleton, inputs, default);
        var exact = Compare(datasetStats, preparedExact);
        _out.WriteLine($"  with the dataset's names and spectral features: mean {exact.Mean:0.00000}, max {exact.Worst:0.00000}");
        if (Environment.GetEnvironmentVariable("T2A_SAMPLER_STUDY") == "1")
            foreach (var (name, alt) in new (string, SampleSettings)[]
            {
                ("old default: Adams-Bashforth 16 calls", new SampleSettings { Method = Integrator.AdamsBashforth2, Steps = 16, TimeShift = 0.5f, Guidance = settings.Guidance }),
                ("Euler 50", new SampleSettings { Method = Integrator.Euler, Steps = 50, Guidance = settings.Guidance }),
                ("Euler 100", new SampleSettings { Method = Integrator.Euler, Steps = 100, Guidance = settings.Guidance }),
                ("dopri5 rtol 1e-2", new SampleSettings { Method = Integrator.Dopri5, RelativeTolerance = 1e-2, AbsoluteTolerance = 1e-5, Guidance = settings.Guidance }),
                ("dopri5 rtol 3e-3", new SampleSettings { Method = Integrator.Dopri5, RelativeTolerance = 3e-3, Guidance = settings.Guidance }),
            })
            {
                var keepSettings = settings;
                settings = alt;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var alternative = Compare(datasetStats, preparedExact);
                settings = keepSettings;
                _out.WriteLine($"  {name} vs upstream's dopri5: mean {alternative.Mean:0.0000}, max {alternative.Worst:0.0000} ({watch.ElapsedMilliseconds} ms)");
            }

        // fixed-step references must match to rounding; dopri5 ones up to the solver's own tolerance: upstream pads the
        // batch to 71 joint slots whose (meaningless) velocities enter its RMS error norm and so its step sizes
        // (the solver itself matches torchdiffeq exactly: Dopri5Tests)
        var limit = settings.Method == Integrator.Dopri5 ? 0.06f : 5e-3f;
        Assert.True(exact.Worst < limit, $"same inputs, different motion: {exact.Worst}");
        if (dataset != "objaverse") // an Objaverse skeleton may look like a person or an animal; upstream still uses Objaverse's
            Assert.Equal(expected, uni.Family);
        // the dataset's curated joint names differ from what upstream's own name rule (the port's) makes of a new rig;
        // with the same names and facing, the port's motion must stand where UniMate's does
        if (nameDiffs == 0 && faceSame)
            Assert.True(own.GroundDiff < 0.05f, $"the port's motion stands {own.GroundDiff:0.000} off where UniMate's does");
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TextToAnimation.Editor.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// How smooth generated motion ends up, on the stored generated sequences (fixtures/upstream_cleanup: a fox and the
/// s&amp;box human, raw from UniMate, with their mesh capsules): jerk (joints' acceleration), the deepest body overlap,
/// the closest hind-to-front paw approach and stance foot slide - raw, after UniMate's clean-up, and after the editor's
/// full clean-up (smoothing around it).
/// </summary>
public class SmoothnessTests
{
    readonly ITestOutputHelper _out; public SmoothnessTests(ITestOutputHelper o) => _out = o;

    public sealed record Metrics(float Jerk, float Overlap, float Clearance, float Slide);

    static float Size(UniMateCleanup.Skeleton s) { var lo = s.Heads.Aggregate(Vector3.Min); var hi = s.Heads.Aggregate(Vector3.Max); return (hi - lo).Length(); }

    public static Metrics Measure(UniMateCleanup.Motion m, UniMateCleanup.Skeleton s, string[] hind, string[] front)
    {
        var size = Size(s);
        var n = m.Frames;
        // jerk: per frame the largest joint acceleration, median over frames, per the rig's size (30 fps)
        var acc = new List<float>();
        for (var t = 1; t < n - 1; t++)
            acc.Add(Enumerable.Range(0, s.Count).Max(j => (m.Pos[t + 1][j] - 2 * m.Pos[t][j] + m.Pos[t - 1][j]).Length()) / size * 900f);
        acc.Sort();
        var jerk = acc[acc.Count / 2];
        float clearance = float.NaN;
        if (front is not null)
            clearance = hind.Zip(front).Min(p => Enumerable.Range(0, n).Min(t => (m.Pos[t][Array.IndexOf(s.Names, p.First)] - m.Pos[t][Array.IndexOf(s.Names, p.Second)]).Length())) / size;
        var body = Enumerable.Range(1, n - 1).Average(t => Flat(m.Pos[t][0] - m.Pos[t - 1][0]).Length());
        // foot skating: every foot the clean-up stands on (a quadruped's four paws), its speed along the ground while it
        // is down (within 2% of the rig's size of the lowest any of them comes), against the body's
        var ground = s.Profiles.Min(p => Enumerable.Range(0, n).Min(t => m.Pos[t][p.Joint].Z));
        var down = s.Profiles.SelectMany(p => Enumerable.Range(1, n - 1)
            .Where(t => Math.Max(m.Pos[t][p.Joint].Z, m.Pos[t - 1][p.Joint].Z) - (ground + s.Heads[p.Joint].Z - s.Profiles.Min(q => s.Heads[q.Joint].Z)) < .02f * size)
            .Select(t => Flat(m.Pos[t][p.Joint] - m.Pos[t - 1][p.Joint]).Length())).ToList();
        var slide = down.Count == 0 ? 0f : down.Average() / Math.Max(body, 1e-6f);
        return new Metrics(jerk, UniMateCleanup.Overlap(m, s), clearance, slide);
    }

    static Vector3 Flat(Vector3 v) => v with { Z = 0 };

    public static IEnumerable<object[]> Rigs() => new[]
    {
        new object[] { "fox", new[] { "b_LeftFoot02_018", "b_RightFoot02_022" }, new[] { "b_LeftHand_011", "b_RightHand_08" } },
        new object[] { "citizen", new[] { "ankle_L", "ankle_R" }, null },
    };

    /// <summary>The editor's clean-up leaves a motion smoother than UniMate's raw output, keeps UniMate's clean-up's fixes.</summary>
    [Theory]
    [MemberData(nameof(Rigs))]
    public void TheEditorsCleanupIsSmootherThanRawAndKeepsItsFixes(string rig, string[] hind, string[] front)
    {
        var (s, raw, cursors) = UpstreamCleanupTests.LoadInput(rig);
        s.IgnoreRestOverlaps = true; s.GroundedStances = true;
        UniMateCleanup.Joins(raw, s, cursors);
        var r = Measure(raw, s, hind, front);

        var unimate = UniMateCleanup.Copy(raw);
        UniMateCleanup.Clean(unimate, s);
        var u = Measure(unimate, s, hind, front);

        var full = UniMateCleanup.Copy(raw);
        _out.WriteLine($"{rig} editor clean-up report: {UniMateCleanup.Smoothed(full, s)}");
        var f = Measure(full, s, hind, front);

        // sweep (T2A_SMOOTH_SWEEP=1): motion and correction smoothing strengths
        if (Environment.GetEnvironmentVariable("T2A_SMOOTH_SWEEP") == "1")
            foreach (var ms in new[] { 0f, 1f, 1.5f, 2f, 2.5f, 3f })
                foreach (var cs in new[] { 0f, 1f, 2f, 3f, 4f })
                {
                    var v = UniMateCleanup.Copy(raw);
                    UniMateCleanup.Smoothed(v, s, ms, cs);
                    _out.WriteLine($"{rig} motion {ms} corrections {cs}: {Measure(v, s, hind, front)}");
                }
        _out.WriteLine($"{rig} raw:              {r}");
        _out.WriteLine($"{rig} UniMate clean-up: {u}");
        _out.WriteLine($"{rig} editor clean-up:  {f}");
        Assert.True(f.Jerk < r.Jerk * .8f, $"{rig}: jerk {r.Jerk:0.0} raw -> {f.Jerk:0.0}");
        Assert.True(f.Overlap <= u.Overlap + .05f * Size(s), $"{rig}: overlap {u.Overlap:0.00} -> {f.Overlap:0.00}");
        if (front is not null) Assert.True(f.Clearance >= u.Clearance * .8f, $"{rig}: paw clearance {u.Clearance:0.000} -> {f.Clearance:0.000}");
        Assert.True(f.Slide <= Math.Max(u.Slide, r.Slide) * 1.1f, $"{rig}: stance slide {u.Slide:0.00} -> {f.Slide:0.00}");
    }
}

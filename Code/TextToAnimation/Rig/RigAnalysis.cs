#nullable enable annotations

using System.Numerics;

namespace TextToAnimation.Rig;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>Which side of the character's mirror plane a bone is on.</summary>
public enum BoneSide { Center, Left, Right }

/// <summary>What a bone is part of, found from the skeleton's shape.</summary>
public enum RigPart { Excluded, Root, Hips, Spine, Neck, Head, HeadPart, Tail, Arm, Leg, FrontLeg, Wing, Fin, Digit, Other }

/// <summary>A limb kind, found from where a mirrored chain attaches and whether it reaches the ground.</summary>
public enum LimbKind { Leg, FrontLeg, Arm, Wing, HeadPart, Fin, Other }

/// <summary>How the character's facing direction is measured (UniMate's face joints).</summary>
public enum RigFacing { None, Pair, BodyAxis }

/// <summary>One limb: a chain of bones hanging off the central body.</summary>
public sealed class RigLimb
{
    public required BoneSide Side { get; init; }
    public required LimbKind Kind { get; set; }
    /// <summary>The central bone the limb hangs from.</summary>
    public required int Attach { get; init; }
    /// <summary>Main chain from the limb root outwards; ends at the fan bone (hand, foot) when the limb has digits.</summary>
    public required List<int> Chain { get; init; }
    /// <summary>Bones below the chain's end (fingers, toes) and side branches.</summary>
    public required List<int> Digits { get; init; }
    public required bool Grounded { get; init; }
    /// <summary>Sum of the chain's bone lengths.</summary>
    public required float Length { get; init; }
    /// <summary>The mirrored limb on the other side, or null.</summary>
    public RigLimb? Partner { get; set; }
}

/// <summary>
/// Reads a skeleton's anatomy from its rest pose alone, without using bone names, so any armature works:
/// humans, birds, dinosaurs, quadrupeds, snakes, rigs with bones called "bone_017". It finds
/// <list type="bullet">
/// <item>the body tree (the biggest root, minus IK targets, attachment points and twist helpers, which are
/// recognised by sitting on other bones or along a bone),</item>
/// <item>the mirror plane (the vertical plane the skeleton is most symmetric about) and left/right partners,</item>
/// <item>forward (toes point forward; then names if they carry sides; then s&amp;box's +X convention) and up (+Z),</item>
/// <item>the hips (where the main branches split), the spine, neck, head and tails along the centre line, and
/// the limbs (mirrored chains: legs reach the ground, arms or wings don't),</item>
/// <item>a training-vocabulary label for every bone ("Left Thigh", "Tail", "Right Wing", "Head"), with the
/// cleaned bone name used only where the shape says nothing.</item>
/// </list>
/// Engine space: Z up, inches. Built once per rig.
/// </summary>
public sealed class RigAnalysis
{
    public Skeleton Skeleton { get; }
    public Vector3 Up { get; } = Vector3.UnitZ;
    public Vector3 Forward { get; private set; } = Vector3.UnitX;
    /// <summary>Character left (Up x Forward).</summary>
    public Vector3 Left { get; private set; } = Vector3.UnitY;

    /// <summary>True when the rig has a mirror plane with left/right partner bones.</summary>
    public bool Symmetric { get; private set; }
    /// <summary>The mirror plane: dot(<see cref="MirrorNormal"/>, p) = <see cref="MirrorOffset"/>; the normal is ±Left.</summary>
    public Vector3 MirrorNormal { get; private set; } = Vector3.UnitY;
    public float MirrorOffset { get; private set; }

    /// <summary>The parentless bone of the body tree.</summary>
    public int PrimaryRoot { get; private set; }
    /// <summary>The hips: where the body's main branches split (UniMate's root joint).</summary>
    public int BodyRoot { get; private set; }
    /// <summary>The head (end of the spine), or -1.</summary>
    public int Head { get; private set; } = -1;

    /// <summary>Bones that carry the body's motion (in the body tree and not a helper).</summary>
    public bool[] InBody { get; }
    /// <summary>Mirror partner of each bone (itself for centre and unpaired bones).</summary>
    public int[] Mirror { get; }
    public BoneSide[] Side { get; }
    public RigPart[] Part { get; }
    /// <summary>Training-vocabulary label per bone ("Left Thigh"); "Bone" when nothing is known.</summary>
    public string[] Label { get; }

    public List<int> SpineChain { get; } = new();
    public List<List<int>> Tails { get; } = new();
    public List<RigLimb> Limbs { get; } = new();

    public RigFacing Facing { get; private set; }
    /// <summary>Face joints: the right/left pair, or head/tail for <see cref="RigFacing.BodyAxis"/>.</summary>
    public int FacingRight { get; private set; } = -1;
    public int FacingLeft { get; private set; } = -1;

    /// <summary>Two legs on the ground, two arms, a head above the hips.</summary>
    public bool IsHumanoid { get; private set; }
    /// <summary>Rest height of the body tree (top minus bottom along Up).</summary>
    public float Height { get; private set; }
    /// <summary>Diagonal of the body tree's bounding box (the tolerance scale).</summary>
    public float Size { get; private set; }
    /// <summary>Lowest point of the body tree along Up.</summary>
    public float GroundHeight { get; private set; }
    /// <summary>How forward was decided ("feet", "names", "convention").</summary>
    public string ForwardSource { get; private set; } = "convention";

    readonly Vector3[] _p;
    readonly List<int>[] _children;
    readonly int[] _depth;

    RigAnalysis(Skeleton skeleton)
    {
        Skeleton = skeleton;
        var n = skeleton.Count;
        _p = new Vector3[n];
        for (var i = 0; i < n; i++) _p[i] = skeleton.RestWorld[i].Pos;
        _children = new List<int>[n];
        for (var i = 0; i < n; i++) _children[i] = new List<int>();
        _depth = new int[n];
        for (var i = 0; i < n; i++)
        {
            var parent = skeleton[i].ParentIndex;
            if (parent >= 0) _children[parent].Add(i);
        }
        for (var i = 0; i < n; i++)
        {
            var d = 0;
            for (var q = skeleton[i].ParentIndex; q >= 0; q = skeleton[q].ParentIndex) d++;
            _depth[i] = d;
        }
        InBody = new bool[n];
        Mirror = new int[n];
        Side = new BoneSide[n];
        Part = new RigPart[n];
        Label = new string[n];
        for (var i = 0; i < n; i++) { Mirror[i] = i; Label[i] = "Bone"; }
    }

    /// <summary>Analyses an engine-space skeleton (Z up).</summary>
    public static RigAnalysis Analyze(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var a = new RigAnalysis(skeleton);
        if (skeleton.Count == 0) return a;
        a.FindBodyTree();
        a.FindMirrorPlane();
        a.PairBones();
        a.FindForward();
        a.AssignSides();
        a.FindBodyRoot();
        a.FindCentralChains();
        a.FindLimbs();
        a.LabelBones();
        a.ChooseFacing();
        return a;
    }

    // ------------------------------------------------------------------ queries

    public Vector3 Position(int bone) => _p[bone];

    /// <summary>Body children of a bone.</summary>
    public IEnumerable<int> BodyChildren(int bone) => _children[bone].Where(c => InBody[c]);

    public bool IsAncestor(int ancestor, int bone)
    {
        for (var q = Skeleton[bone].ParentIndex; q >= 0; q = Skeleton[q].ParentIndex)
            if (q == ancestor) return true;
        return false;
    }

    /// <summary>Body bones in the subtree of <paramref name="bone"/>, including it.</summary>
    public IEnumerable<int> BodySubtree(int bone)
    {
        var stack = new Stack<int>();
        stack.Push(bone);
        while (stack.Count > 0)
        {
            var b = stack.Pop();
            if (!InBody[b]) continue;
            yield return b;
            for (var i = _children[b].Count - 1; i >= 0; i--) stack.Push(_children[b][i]);
        }
    }

    /// <summary>Signed distance from the mirror plane (positive on the <see cref="MirrorNormal"/> side).</summary>
    public float Lateral(int bone) => Vector3.Dot(MirrorNormal, _p[bone]) - MirrorOffset;

    /// <summary>The farthest body descendant of <paramref name="bone"/> (excluding itself), or -1.</summary>
    public int FarthestBodyDescendant(int bone)
    {
        var best = -1; var bestDist = 0f;
        foreach (var d in BodySubtree(bone))
        {
            if (d == bone) continue;
            var dist = (_p[d] - _p[bone]).Length();
            if (dist > bestDist) { bestDist = dist; best = d; }
        }
        return best;
    }

    /// <summary>The hind leg pair (left, right) or nulls.</summary>
    public (RigLimb? Left, RigLimb? Right) MainLegs()
    {
        var legs = Limbs.Where(l => l.Kind == LimbKind.Leg).ToList();
        return (legs.FirstOrDefault(l => l.Side == BoneSide.Left), legs.FirstOrDefault(l => l.Side == BoneSide.Right));
    }

    // ------------------------------------------------------------------ body tree and helpers

    void FindBodyTree()
    {
        var n = Skeleton.Count;
        var size = new int[n];
        for (var i = 0; i < n; i++) size[i] = SubtreeCount(i);
        var roots = Enumerable.Range(0, n).Where(i => Skeleton[i].ParentIndex < 0).ToList();
        PrimaryRoot = roots.OrderByDescending(r => size[r]).ThenBy(r => r).First();
        foreach (var b in Subtree(PrimaryRoot)) InBody[b] = true;
        Measure();

        // IK targets / attachment points: bones sitting on a deeper bone of another branch (and their subtrees)
        var tolerance = 0.004f * Size;
        var bodyBones = Enumerable.Range(0, n).Where(i => InBody[i]).ToList();
        foreach (var b in bodyBones)
        {
            if (!InBody[b]) continue;
            foreach (var o in bodyBones)
            {
                if (o == b || !InBody[o] || _depth[o] - _depth[b] < 2) continue;
                if (IsAncestor(b, o) || IsAncestor(o, b)) continue;
                if ((_p[b] - _p[o]).Length() > tolerance) continue;
                foreach (var h in Subtree(b)) InBody[h] = false;
                break;
            }
        }

        // zero-length leaves and helpers that lie along their parent's bone (twist, elbow/knee helpers)
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var b in bodyBones)
            {
                if (!InBody[b] || b == PrimaryRoot || BodyChildren(b).Any()) continue;
                var parent = Skeleton[b].ParentIndex;
                if (parent < 0) continue;
                var offset = (_p[b] - _p[parent]).Length();
                if (offset < 0.005f * Size) { InBody[b] = false; changed = true; continue; }
                var main = BodyChildren(parent).Where(c => c != b).OrderByDescending(c => BodySubtree(c).Count()).ThenBy(c => c).FirstOrDefault(-1);
                if (main < 0) continue;
                var seg = _p[main] - _p[parent];
                var segLen = seg.Length();
                if (segLen < 1e-6f) continue;
                var rel = _p[b] - _p[parent];
                var t = Vector3.Dot(rel, seg) / (segLen * segLen);
                var off = (rel - seg * t).Length();
                // twist bones sit on the bone's axis, elbow/knee helpers at the joint; a toe, thumb, jaw or spike sticks out
                var onAxis = t > -0.15f && t < 1.05f && off < MathF.Max(0.06f * segLen, 0.004f * Size) && offset < 1.05f * segLen;
                var atJoint = offset < 0.12f * segLen;
                if (onAxis || atJoint)
                {
                    InBody[b] = false;
                    changed = true;
                }
            }
        }
        Measure();
    }

    void Measure()
    {
        var bones = Enumerable.Range(0, Skeleton.Count).Where(i => InBody[i]).ToList();
        if (bones.Count == 0) { Size = 1; Height = 1; return; }
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var b in bones) { min = Vector3.Min(min, _p[b]); max = Vector3.Max(max, _p[b]); }
        Size = MathF.Max((max - min).Length(), 1e-4f);
        Height = max.Z - min.Z;
        if (Height < 0.05f * Size) Height = Size;
        GroundHeight = min.Z;
    }

    int SubtreeCount(int bone) => Subtree(bone).Count();

    IEnumerable<int> Subtree(int bone)
    {
        var stack = new Stack<int>();
        stack.Push(bone);
        while (stack.Count > 0)
        {
            var b = stack.Pop();
            yield return b;
            foreach (var c in _children[b]) stack.Push(c);
        }
    }

    List<int> BodyBones() => Enumerable.Range(0, Skeleton.Count).Where(i => InBody[i]).ToList();

    // ------------------------------------------------------------------ mirror plane and partners

    void FindMirrorPlane()
    {
        var bones = BodyBones();
        if (bones.Count < 3) return;
        var root = _p[PrimaryRoot];
        (float Cost, float Offset) Score(float degrees)
        {
            var r = degrees * MathF.PI / 180f;
            var normal = new Vector3(MathF.Cos(r), MathF.Sin(r), 0);
            var lat = bones.Select(b => Vector3.Dot(normal, _p[b])).OrderBy(x => x).ToList();
            var offset = lat[lat.Count / 2];
            var cost = 0f;
            foreach (var b in bones)
            {
                var reflected = _p[b] - 2f * (Vector3.Dot(normal, _p[b]) - offset) * normal;
                var nearest = float.MaxValue;
                foreach (var o in bones) nearest = MathF.Min(nearest, (reflected - _p[o]).LengthSquared());
                cost += MathF.Sqrt(nearest);
            }
            cost /= bones.Count;
            // the root lies on a real mirror plane (a straight chain is also "symmetric" end to end)
            cost += 0.5f * MathF.Abs(Vector3.Dot(normal, root) - offset);
            return (cost, offset);
        }
        var best = 0f; var bestCost = float.MaxValue;
        for (var deg = 0; deg < 180; deg++)
        {
            var (cost, _) = Score(deg);
            if (cost < bestCost - 1e-7f) { bestCost = cost; best = deg; }
        }
        var center = best;
        for (var deg = center - 1f; deg <= center + 1f; deg += 0.05f)
        {
            var (cost, _) = Score(deg);
            if (cost < bestCost - 1e-7f) { bestCost = cost; best = deg; }
        }
        var rad = best * MathF.PI / 180f;
        var normal = new Vector3(MathF.Cos(rad), MathF.Sin(rad), 0);
        normal = SnapAxis(normal);
        var lats = bones.Select(b => Vector3.Dot(normal, _p[b])).OrderBy(x => x).ToList();
        MirrorNormal = normal;
        MirrorOffset = lats[lats.Count / 2];
        var offPlane = bones.Count(b => MathF.Abs(Lateral(b)) > 0.01f * Size);
        Symmetric = bestCost < 0.012f * Size && offPlane >= 2;
    }

    static Vector3 SnapAxis(Vector3 v)
    {
        var a = Vector3.Abs(v);
        if (a.Y < 2e-3f && a.Z < 2e-3f) return new Vector3(MathF.Sign(v.X), 0, 0);
        if (a.X < 2e-3f && a.Z < 2e-3f) return new Vector3(0, MathF.Sign(v.Y), 0);
        return Vector3.Normalize(v);
    }

    void PairBones()
    {
        if (!Symmetric) return;
        var bones = BodyBones();
        var tc = 0.01f * Size;
        var pos = bones.Where(b => Lateral(b) > tc).ToList();
        var neg = bones.Where(b => Lateral(b) < -tc).ToList();
        var candidates = new List<(float Cost, int A, int B)>();
        foreach (var a in pos)
        {
            var reflected = _p[a] - 2f * Lateral(a) * MirrorNormal;
            foreach (var b in neg)
            {
                var cost = (reflected - _p[b]).Length() + 0.01f * Size * Math.Abs(_depth[a] - _depth[b]);
                if (cost < 0.03f * Size) candidates.Add((cost, a, b));
            }
        }
        foreach (var (_, a, b) in candidates.OrderBy(c => c.Cost).ThenBy(c => c.A).ThenBy(c => c.B))
        {
            if (Mirror[a] != a || Mirror[b] != b) continue;
            Mirror[a] = b; Mirror[b] = a;
        }
        if (!bones.Any(b => Mirror[b] != b)) Symmetric = false;
    }

    // ------------------------------------------------------------------ forward

    void FindForward()
    {
        var bones = BodyBones();
        Vector3 axis;
        if (Symmetric) axis = Vector3.Normalize(Vector3.Cross(Up, MirrorNormal));
        else
        {
            // no mirror: the body's long horizontal direction (a snake, a fish)
            axis = PrincipalHorizontal(bones);
        }

        // 1. toes point forward: grounded leaves relative to their parents
        float feet = 0, feetTotal = 0;
        foreach (var b in bones)
        {
            if (BodyChildren(b).Any()) continue;
            if (_p[b].Z > GroundHeight + 0.1f * Height) continue;
            var parent = Skeleton[b].ParentIndex;
            if (parent < 0) continue;
            var v = _p[b] - _p[parent];
            v.Z = 0;
            feet += Vector3.Dot(v, axis);
            feetTotal += v.Length();
        }
        // 2. names that carry a side (bonus)
        var votes = 0; var named = 0;
        if (Symmetric)
        {
            foreach (var b in bones)
            {
                if (Mirror[b] == b) continue;
                var (side, _) = JointNames.SplitSide(JointNames.Clean(Skeleton[b].Name));
                if (side.Length == 0) continue;
                named++;
                // with forward = +axis, left is Up x axis = -MirrorNormal
                var geometricLeft = Lateral(b) < 0;
                votes += (side == "Left") == geometricLeft ? 1 : -1;
            }
        }
        float sign;
        if (feetTotal > 0 && MathF.Abs(feet) > 0.3f * feetTotal && MathF.Abs(feet) > 0.005f * Size) { sign = MathF.Sign(feet); ForwardSource = "feet"; }
        else if (named >= 2 && Math.Abs(votes) >= 0.6f * named) { sign = Math.Sign(votes); ForwardSource = "names"; }
        else if (MathF.Abs(axis.X) > 0.5f) { sign = MathF.Sign(axis.X); ForwardSource = "convention"; }
        else if (MathF.Abs(feet) > 1e-6f) { sign = MathF.Sign(feet); ForwardSource = "feet"; }
        else { sign = 1; ForwardSource = "convention"; }
        Forward = axis * sign;
        Left = Vector3.Normalize(Vector3.Cross(Up, Forward));
    }

    Vector3 PrincipalHorizontal(List<int> bones)
    {
        if (bones.Count < 2) return Vector3.UnitX;
        var mean = Vector3.Zero;
        foreach (var b in bones) mean += _p[b];
        mean /= bones.Count;
        float xx = 0, xy = 0, yy = 0;
        foreach (var b in bones)
        {
            var d = _p[b] - mean;
            xx += d.X * d.X; xy += d.X * d.Y; yy += d.Y * d.Y;
        }
        var angle = 0.5f * MathF.Atan2(2 * xy, xx - yy);
        var v = SnapAxis(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0));
        return v;
    }

    void AssignSides()
    {
        var tc = 0.01f * Size;
        var leftSign = Vector3.Dot(Left, MirrorNormal) >= 0 ? 1f : -1f;
        for (var b = 0; b < Skeleton.Count; b++)
        {
            if (!InBody[b] || !Symmetric) { Side[b] = BoneSide.Center; continue; }
            var lat = Lateral(b) * leftSign;
            Side[b] = lat > tc ? BoneSide.Left : lat < -tc ? BoneSide.Right : BoneSide.Center;
        }
        // bones that cross to the other side of the body from their parent are IK rules, not anatomy
        for (var b = 0; b < Skeleton.Count; b++)
        {
            if (!InBody[b]) continue;
            var parent = Skeleton[b].ParentIndex;
            if (parent < 0 || !InBody[parent] || Side[b] == BoneSide.Center || Side[parent] == BoneSide.Center || Side[b] == Side[parent]) continue;
            foreach (var h in Subtree(b))
            {
                InBody[h] = false;
                Side[h] = BoneSide.Center;
                if (Mirror[h] != h) { Mirror[Mirror[h]] = Mirror[h]; Mirror[h] = h; }
            }
        }
    }

    // ------------------------------------------------------------------ hips, spine, tails

    void FindBodyRoot()
    {
        var r = PrimaryRoot;
        if (!InBody[r]) { BodyRoot = r; return; }
        for (var guard = 0; guard < Skeleton.Count; guard++)
        {
            var kids = BodyChildren(r).ToList();
            if (kids.Count == 0) break;
            if (kids.Any(k => Mirror[k] != k && kids.Contains(Mirror[k]))) break;
            int next;
            if (kids.Count == 1) next = kids[0];
            else
            {
                var total = BodySubtree(r).Count() - 1;
                var big = kids.OrderByDescending(k => BodySubtree(k).Count()).ThenBy(k => k).First();
                if (BodySubtree(big).Count() < 0.85f * total) break;
                next = big;
            }
            var seg = _p[next] - _p[r];
            var len = seg.Length();
            if (len < 0.01f * Size || seg.Z > 0.6f * len) { r = next; continue; }
            break;
        }
        BodyRoot = r;
    }

    bool IsCentral(int bone) => !Symmetric || Side[bone] == BoneSide.Center;

    /// <summary>From <paramref name="start"/>, follows central children with the biggest subtree.</summary>
    List<int> CentralPath(int start)
    {
        var path = new List<int> { start };
        var b = start;
        while (true)
        {
            var next = BodyChildren(b).Where(IsCentral).OrderByDescending(c => BodySubtree(c).Count())
                .ThenByDescending(c => (_p[c] - _p[b]).Length()).ThenBy(c => c).FirstOrDefault(-1);
            if (next < 0) break;
            path.Add(next);
            b = next;
        }
        return path;
    }

    float Along(int bone) => Vector3.Dot(_p[bone], Forward);

    void FindCentralChains()
    {
        var hips = BodyRoot;
        if (!InBody[hips]) return;
        var starts = BodyChildren(hips).Where(IsCentral).ToList();
        if (!Symmetric)
        {
            // a snake or similar: the whole centre line is one chain; the head is its front end
            var path = CentralPath(hips);
            path.RemoveAt(0);
            SpineChain.AddRange(path);
            return;
        }
        var paths = starts.Select(CentralPath).ToList();
        if (paths.Count == 0) return;
        // the spine carries the limbs (arms, wings, front legs, the head's ears); a tail, however long, carries none
        int LimbsBelow(int start) => BodySubtree(start).Count(b => !IsCentral(b) && Skeleton[b].ParentIndex >= 0 && IsCentral(Skeleton[b].ParentIndex));
        var spine = paths.OrderByDescending(p => LimbsBelow(p[0])).ThenByDescending(p => BodySubtree(p[0]).Count()).ThenBy(p => p[0]).First();
        SpineChain.AddRange(spine);
        foreach (var p in paths)
        {
            if (p == spine) continue;
            Tails.Add(p);
        }
    }

    // ------------------------------------------------------------------ limbs

    void FindLimbs()
    {
        var bones = BodyBones();
        var roots = bones.Where(b => !IsCentral(b) && Skeleton[b].ParentIndex >= 0 && InBody[Skeleton[b].ParentIndex] && IsCentral(Skeleton[b].ParentIndex)).ToList();
        foreach (var root in roots)
        {
            var chain = new List<int> { root };
            var b = root;
            while (true)
            {
                var kids = BodyChildren(b).ToList();
                if (kids.Count == 0 || kids.Count >= 3) break; // end, or a fan of digits (hand, foot)
                var next = kids.OrderByDescending(k => BodySubtree(k).Count()).ThenByDescending(k => Reach(k)).ThenBy(k => k).First();
                chain.Add(next);
                b = next;
            }
            var digits = BodySubtree(root).Where(x => !chain.Contains(x)).ToList();
            var lowest = BodySubtree(root).Min(x => _p[x].Z);
            var length = 0f;
            for (var i = 1; i < chain.Count; i++) length += (_p[chain[i]] - _p[chain[i - 1]]).Length();
            Limbs.Add(new RigLimb
            {
                Side = Side[root],
                Kind = LimbKind.Other,
                Attach = Skeleton[root].ParentIndex,
                Chain = chain,
                Digits = digits,
                Grounded = lowest < GroundHeight + 0.12f * Height,
                Length = length,
            });
        }
        foreach (var limb in Limbs)
            limb.Partner = Limbs.FirstOrDefault(o => o != limb && Mirror[o.Chain[0]] == limb.Chain[0]);
    }

    float Reach(int bone)
    {
        var best = 0f;
        foreach (var d in BodySubtree(bone)) best = MathF.Max(best, (_p[d] - _p[bone]).Length());
        return best;
    }

    // ------------------------------------------------------------------ labels

    void LabelBones()
    {
        var n = Skeleton.Count;
        for (var b = 0; b < n; b++) Part[b] = InBody[b] ? RigPart.Other : RigPart.Excluded;
        if (!InBody[BodyRoot]) return;

        // above the hips (a ground-level root bone)
        for (var q = Skeleton[BodyRoot].ParentIndex; q >= 0; q = Skeleton[q].ParentIndex)
            if (InBody[q]) { Part[q] = RigPart.Root; Label[q] = "Root"; }
        Part[BodyRoot] = RigPart.Hips;
        Label[BodyRoot] = "Hips";

        // limbs attached along the spine decide where the chest ends; short ones (ears) don't count
        var spineIndex = new Dictionary<int, int>();
        for (var i = 0; i < SpineChain.Count; i++) spineIndex[SpineChain[i]] = i;
        var chest = -1;
        foreach (var limb in Limbs)
            if (spineIndex.TryGetValue(limb.Attach, out var at) && limb.Length + Reach(limb.Chain[^1]) > 0.15f * Height)
                chest = Math.Max(chest, at);
        _chest = chest >= 0 ? SpineChain[chest] : -1;
        var head = -1;
        if (!Symmetric)
        {
            // a snake: one centre line; its front end is the head, its back end the tail
            foreach (var b in SpineChain) { Part[b] = RigPart.Spine; Label[b] = "Spine"; }
            var line = SpineChain.Concat(new[] { BodyRoot }).ToList();
            if (line.Count >= 3)
            {
                var front = line.OrderByDescending(Along).ThenBy(b => b).First();
                var back = line.OrderBy(Along).ThenBy(b => b).First();
                if (front != BodyRoot) { Head = front; Part[front] = RigPart.Head; Label[front] = "Head"; }
                if (back != BodyRoot && back != front) { Part[back] = RigPart.Tail; Label[back] = "Tail"; }
            }
            ClassifyLimbs();
            foreach (var limb in Limbs) LabelLimb(limb);
            LabelUnexplained();
            return;
        }
        else
        {
            // the head: the first bone past the chest where the centre line branches (jaw, ears, eyes); or the
            // end of the line, unless that end turns sharply away from the neck (a beak, snout or jaw)
            for (var i = chest + 1; i < SpineChain.Count; i++)
            {
                var b = SpineChain[i];
                var kids = BodyChildren(b).ToList();
                if (kids.Count >= 2 || kids.Any(k => !IsCentral(k))) { head = i; break; }
                if (i < SpineChain.Count - 1) continue;
                head = i;
                if (i - 2 >= 0 && i - 1 > chest)
                {
                    var neck = _p[SpineChain[i - 1]] - _p[SpineChain[i - 2]];
                    var tip = _p[b] - _p[SpineChain[i - 1]];
                    if (neck.Length() > 1e-6f && tip.Length() > 1e-6f
                        && Vector3.Dot(Vector3.Normalize(neck), Vector3.Normalize(tip)) < MathF.Cos(35f * MathF.PI / 180f))
                        head = i - 1;
                }
            }
        }
        if (head >= 0) Head = SpineChain[head];
        for (var i = 0; i < SpineChain.Count; i++)
        {
            var b = SpineChain[i];
            if (head >= 0 && i == head) { Part[b] = RigPart.Head; Label[b] = "Head"; }
            else if (head >= 0 && i > head) { Part[b] = RigPart.HeadPart; Label[b] = _p[b].Z < _p[Head].Z ? "Jaw" : "Head"; }
            else if (head >= 0 && i > chest && i < head) { Part[b] = RigPart.Neck; Label[b] = "Neck"; }
            else { Part[b] = RigPart.Spine; Label[b] = "Spine"; }
        }
        foreach (var tail in Tails)
        {
            var behind = Along(tail[^1]) < Along(BodyRoot) - 0.02f * Size || _p[tail[^1]].Z < _p[BodyRoot].Z;
            foreach (var b in tail)
            {
                Part[b] = behind ? RigPart.Tail : RigPart.Spine;
                Label[b] = behind ? "Tail" : "Spine";
            }
        }
        // central bones under the head (jaw, tongue, beak)
        if (Head >= 0)
            foreach (var b in BodySubtree(Head))
                if (b != Head && IsCentral(b) && Part[b] == RigPart.Other) { Part[b] = RigPart.HeadPart; Label[b] = _p[b].Z < _p[Head].Z ? "Jaw" : "Head"; }

        ClassifyLimbs();
        foreach (var limb in Limbs) LabelLimb(limb);
        LabelUnexplained();
        IsHumanoid = DetectHumanoid();
    }

    /// <summary>Anything the shape didn't explain: the cleaned bone name when it is a known word, else "Bone".</summary>
    void LabelUnexplained()
    {
        for (var b = 0; b < Skeleton.Count; b++)
        {
            if (!InBody[b] || Part[b] != RigPart.Other) continue;
            var cleaned = JointNames.Clean(Skeleton[b].Name);
            if (!JointNames.IsKnown(cleaned)) { Label[b] = "Bone"; continue; }
            var (_, baseName) = JointNames.SplitSide(cleaned);
            Label[b] = Sided(b, baseName);
        }
    }

    string Sided(int bone, string label) => Side[bone] switch
    {
        BoneSide.Left => "Left " + label,
        BoneSide.Right => "Right " + label,
        _ => label,
    };

    bool InHeadCluster(int bone) => Head >= 0 && (bone == Head || IsAncestor(Head, bone));

    int _chest = -1;

    /// <summary>An upright body: the spine rises from the hips to the chest (or head), as for a person; birds,
    /// quadrupeds and dinosaurs carry it level.</summary>
    bool Upright
    {
        get
        {
            var top = _chest >= 0 ? _chest : Head;
            if (top < 0) return false;
            var v = _p[top] - _p[BodyRoot];
            return v.Z > 0.7f * v.Length() && v.Z > 0.15f * Height;
        }
    }

    void ClassifyLimbs()
    {
        var tailBones = new HashSet<int>(Tails.Where(t => t.Count > 0 && Part[t[0]] == RigPart.Tail).SelectMany(t => t));
        foreach (var limb in Limbs)
        {
            if (InHeadCluster(limb.Attach)) limb.Kind = LimbKind.HeadPart;
            else if (tailBones.Contains(limb.Attach)) limb.Kind = LimbKind.Fin;
            else if (limb.Grounded) limb.Kind = LimbKind.Leg;
        }
        // with two or more grounded pairs the one nearest the hips is the hind legs, the rest front legs
        var legs = Limbs.Where(l => l.Kind == LimbKind.Leg).ToList();
        if (legs.Count > 2)
        {
            int Hops(RigLimb l)
            {
                var hops = 0;
                for (var q = l.Attach; q >= 0 && q != BodyRoot; q = Skeleton[q].ParentIndex) hops++;
                return hops;
            }
            var hind = legs.Min(Hops);
            foreach (var l in legs) if (Hops(l) > hind) l.Kind = LimbKind.FrontLeg;
        }
        var maxLeg = Limbs.Where(l => l.Kind is LimbKind.Leg).Select(l => l.Length).DefaultIfEmpty(0).Max();
        var groundedPairs = Limbs.Count(l => l.Kind is LimbKind.Leg or LimbKind.FrontLeg && l.Side == BoneSide.Left);
        foreach (var limb in Limbs.Where(l => l.Kind == LimbKind.Other))
        {
            if (Upright) limb.Kind = LimbKind.Arm;
            else if (groundedPairs <= 1 && maxLeg > 0 && limb.Length >= 0.5f * maxLeg) limb.Kind = LimbKind.Wing;
            else limb.Kind = LimbKind.Arm;
        }
    }

    void LabelLimb(RigLimb limb)
    {
        var k = limb.Chain.Count;
        string[] names;
        string digit;
        RigPart part;
        switch (limb.Kind)
        {
            case LimbKind.Leg:
                names = LimbSegments(limb, "Hip", "Fetlock", new[] { "Thigh", "Shin", "Foot", "Toe" });
                digit = "Toe"; part = RigPart.Leg; break;
            case LimbKind.FrontLeg:
                names = LimbSegments(limb, "Shoulder", "Metacarpus", new[] { "Upper Arm", "Forearm", "Hand", "Finger" });
                digit = "Finger"; part = RigPart.FrontLeg; break;
            case LimbKind.Arm:
                names = k >= 4
                    ? Enumerable.Repeat("Shoulder", k - 3).Concat(new[] { "Upper Arm", "Forearm", "Hand" }).ToArray()
                    : new[] { "Upper Arm", "Forearm", "Hand" }.Take(k).ToArray();
                digit = "Finger"; part = RigPart.Arm; break;
            case LimbKind.Wing:
                names = Enumerable.Repeat("Wing", k).ToArray();
                digit = "Wing"; part = RigPart.Wing; break;
            case LimbKind.HeadPart:
                var above = _p[limb.Chain[0]].Z > _p[Head].Z || Along(limb.Chain[0]) < Along(Head);
                names = Enumerable.Repeat(above ? "Ear" : "Eye", k).ToArray();
                digit = above ? "Ear" : "Eye"; part = RigPart.HeadPart; break;
            case LimbKind.Fin:
                names = Enumerable.Repeat("Fin", k).ToArray();
                digit = "Fin"; part = RigPart.Fin; break;
            default:
                names = Enumerable.Repeat("Bone", k).ToArray();
                digit = "Bone"; part = RigPart.Other; break;
        }
        for (var i = 0; i < k; i++)
        {
            var b = limb.Chain[i];
            Part[b] = part;
            Label[b] = Sided(b, names[i]);
        }
        foreach (var b in limb.Digits)
        {
            Part[b] = part == RigPart.Other ? RigPart.Other : RigPart.Digit;
            Label[b] = Sided(b, digit);
        }
    }

    /// <summary>
    /// Labels for a leg chain: upper, middle, end, digit. A leg with more than four bones either starts with a short
    /// girdle bone (<paramref name="girdle"/>: a hip or shoulder blade) or has extra bones in the lower leg
    /// (<paramref name="middle"/>: the fetlock of a hoofed or digitigrade leg).
    /// </summary>
    string[] LimbSegments(RigLimb limb, string girdle, string middle, string[] four)
    {
        var c = limb.Chain;
        var k = c.Count;
        if (k <= 4) return four.Take(k).ToArray();
        float Seg(int i) => (_p[c[i + 1]] - _p[c[i]]).Length();
        var girdles = 0;
        while (k - girdles > 4 && Seg(girdles) < 0.6f * Seg(girdles + 1)) girdles++;
        var extra = k - girdles - 4;
        return Enumerable.Repeat(girdle, girdles)
            .Concat(four.Take(2))
            .Concat(Enumerable.Repeat(middle, extra))
            .Concat(four.Skip(2))
            .ToArray();
    }

    bool DetectHumanoid()
    {
        var legs = Limbs.Where(l => l.Kind == LimbKind.Leg).ToList();
        var arms = Limbs.Where(l => l.Kind == LimbKind.Arm).ToList();
        if (legs.Count != 2 || arms.Count != 2 || Limbs.Any(l => l.Kind is LimbKind.FrontLeg or LimbKind.Wing)) return false;
        if (legs[0].Side == legs[1].Side || arms[0].Side == arms[1].Side) return false;
        if (legs.Any(l => l.Chain.Count < 3) || arms.Any(l => l.Chain.Count < 3)) return false;
        return Upright && Tails.All(t => t.Count == 0 || Part[t[0]] != RigPart.Tail || t.Count <= 1);
    }

    // ------------------------------------------------------------------ facing

    void ChooseFacing()
    {
        if (Symmetric)
        {
            foreach (var word in JointNames.SymmetricPairPriority)
            {
                var right = Enumerable.Range(0, Skeleton.Count)
                    .Where(b => InBody[b] && Side[b] == BoneSide.Right && Mirror[b] != b && Label[b] == "Right " + word)
                    .OrderBy(b => _depth[b]).ThenBy(b => b).FirstOrDefault(-1);
                if (right < 0) continue;
                Facing = RigFacing.Pair;
                FacingRight = right;
                FacingLeft = Mirror[right];
                return;
            }
            // mirrored bones without a vocabulary label: the pair closest to the hips
            var any = Enumerable.Range(0, Skeleton.Count)
                .Where(b => InBody[b] && Side[b] == BoneSide.Right && Mirror[b] != b)
                .OrderBy(b => _depth[b]).ThenBy(b => b).FirstOrDefault(-1);
            if (any >= 0) { Facing = RigFacing.Pair; FacingRight = any; FacingLeft = Mirror[any]; return; }
        }
        // no mirror: the head and tail ends of the centre line
        var line = SpineChain.Concat(new[] { BodyRoot }).Where(b => InBody[b]).ToList();
        if (line.Count >= 2)
        {
            var front = line.OrderByDescending(Along).First();
            var back = line.OrderBy(Along).First();
            if (front != back && Along(front) - Along(back) > 0.3f * Size)
            {
                Facing = RigFacing.BodyAxis;
                FacingRight = front;
                FacingLeft = back;
                return;
            }
        }
        Facing = RigFacing.None;
    }
}

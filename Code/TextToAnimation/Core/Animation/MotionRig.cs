#nullable enable annotations

using System.Numerics;
using TextToAnimation.Core.Mapping;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;
using TextToAnimation.Core.Rig;

namespace TextToAnimation.Core.Animation;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>
/// Everything the editor knows about one character skeleton: the bones (engine space - inches, Z-up,
/// parent-local rest), the humanoid role mapping, the motion root / hips, foot chains for ground
/// contact work, the character frame and body regions. Built once per workspace and shared by the clip
/// operations, the quality checks, the processing passes and the motion generator.
/// </summary>
public sealed class MotionRig
{
    /// <summary>Engine units per centimeter (s&amp;box: 1 inch = 2.54 cm).</summary>
    public const float EngineUnitsPerCm = 0.3937f;

    public Skeleton Skeleton { get; }
    public TargetRig Rig { get; }
    public MappingResult Map { get; }

    /// <summary>Hips/pelvis bone, or -1 when the skeleton has no recognisable hips.</summary>
    public int HipsIndex { get; }

    /// <summary>
    /// The bone that carries root motion: the topmost parentless animated ancestor of the hips
    /// (a dedicated "root" bone when the rig has one, otherwise the hips themselves).
    /// </summary>
    public int RootIndex { get; }

    /// <summary>World up of the character, snapped to the dominant axis (+Z for engine-space rigs).</summary>
    public Vector3 Up { get; }

    /// <summary>Character forward (toe direction), perpendicular to <see cref="Up"/>.</summary>
    public Vector3 Forward { get; }

    /// <summary>Character left, perpendicular to <see cref="Up"/> and <see cref="Forward"/>.</summary>
    public Vector3 Lateral { get; }

    /// <summary>Rest height of the hips above the feet, in skeleton units.</summary>
    public float HipHeight { get; }

    /// <summary>Skeleton units per centimeter (thresholds in the processing passes are tuned in cm).</summary>
    public float UnitsPerCm { get; }

    public FootChain? LeftFoot { get; }
    public FootChain? RightFoot { get; }

    /// <summary>Every leg that reaches the ground (two for a human or bird, four for a quadruped).</summary>
    public IReadOnlyList<FootChain> Feet { get; }

    /// <summary>The skeleton read from its shape alone (works for any armature and any bone names).</summary>
    public RigAnalysis Analysis { get; }

    /// <summary>True when the bone roles came from the bone names (a recognised humanoid rig).</summary>
    public bool HasNamedRoles { get; }

    /// <summary>True when the mapping found a complete humanoid (hips, spine, legs, arms, head).</summary>
    public bool IsHumanoid { get; }

    /// <summary>Human readable problems found while analysing the skeleton (shown in the quality panel).</summary>
    public IReadOnlyList<string> Problems { get; }

    readonly BodyRegion[] _regions;
    readonly bool[] _motionBones;

    MotionRig(Skeleton skeleton, TargetRig rig, MappingResult map, float unitsPerCm, List<string> problems)
    {
        Skeleton = skeleton;
        Rig = rig;
        Map = map;
        UnitsPerCm = unitsPerCm;
        Problems = problems;
        Analysis = RigAnalysis.Analyze(skeleton);

        if (!IsRoleHumanoid(map) || !Analysis.IsHumanoid)
        {
            // any other armature (a creature, or a humanoid whose bone names aren't recognised): the shape decides
            var a = Analysis;
            HasNamedRoles = false;
            problems.RemoveAll(p => p.StartsWith("Bone roles were only partly", StringComparison.Ordinal));
            HipsIndex = a.InBody[a.BodyRoot] ? a.BodyRoot : -1;
            var top = a.BodyRoot;
            for (var p = skeleton[a.BodyRoot].ParentIndex; p >= 0; p = skeleton[p].ParentIndex)
                if (a.InBody[p]) top = p;
            RootIndex = top;
            Up = a.Up;
            Forward = a.Forward;
            Lateral = a.Left;
            HipHeight = Vector3.Dot(skeleton.RestWorld[a.BodyRoot].Pos, Up) - a.GroundHeight;
            var feet = a.Limbs.Where(l => l.Kind is LimbKind.Leg or LimbKind.FrontLeg).Select(FootOf).Where(f => f is not null).Select(f => f!).ToList();
            Feet = feet;
            var (left, right) = a.MainLegs();
            LeftFoot = left is null ? null : FootOf(left);
            RightFoot = right is null ? null : FootOf(right);
            IsHumanoid = a.IsHumanoid;
            if (feet.Count == 0)
                problems.Add("No legs reach the ground in the rest pose, so the foot tools (foot sliding, grounding, footsteps) are off.");
            _regions = new BodyRegion[skeleton.Count];
            _motionBones = new bool[skeleton.Count];
            for (var i = 0; i < skeleton.Count; i++)
            {
                _regions[i] = RegionFromShape(i);
                _motionBones[i] = a.InBody[i] && rig.ClassOf(i) == BoneClass.Animated;
            }
            return;
        }
        HasNamedRoles = true;

        HipsIndex = map.RoleToBone.TryGetValue(BoneRole.Hips, out var hips) ? hips : -1;
        RootIndex = HipsIndex >= 0 ? MotionRootOf(skeleton, rig, HipsIndex) : FirstRoot(skeleton);

        var up = Vector3.UnitZ;
        var forward = Vector3.UnitX;
        var hipHeight = 0f;
        try
        {
            var frame = CharacterFrame.Compute(skeleton, map, skeleton.RestWorld);
            up = SnapToAxis(frame.Up);
            forward = Vector3.Normalize(frame.Forward - Vector3.Dot(frame.Forward, up) * up);
            hipHeight = frame.HipHeight;
        }
        catch (ArgumentException ex)
        {
            problems.Add($"Could not derive the character's up/forward directions: {ex.Message}");
        }
        if (!IsFinite(forward) || forward.LengthSquared() < 0.5f) forward = Vector3.UnitX;
        Up = up;
        Forward = forward;
        Lateral = Vector3.Normalize(Vector3.Cross(up, forward));
        HipHeight = hipHeight;

        LeftFoot = Chain(map, BoneRole.UpperLegL, BoneRole.LowerLegL, BoneRole.FootL, BoneRole.ToeL);
        RightFoot = Chain(map, BoneRole.UpperLegR, BoneRole.LowerLegR, BoneRole.FootR, BoneRole.ToeR);

        IsHumanoid = true;
        Feet = new[] { LeftFoot!, RightFoot! };

        _regions = new BodyRegion[skeleton.Count];
        _motionBones = new bool[skeleton.Count];
        for (var i = 0; i < skeleton.Count; i++)
        {
            _regions[i] = ComputeRegion(i);
            _motionBones[i] = rig.ClassOf(i) == BoneClass.Animated && !SboxBoneClassifier.IsFaceBone(skeleton[i].Name);
        }
    }

    /// <summary>Builds the rig context for an engine-space skeleton (inches, Z-up) read from a compiled model.</summary>
    public static MotionRig Create(Skeleton skeleton, float unitsPerCm = EngineUnitsPerCm)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var problems = new List<string>();
        var map = ProfileDetector.Detect(skeleton)?.Result ?? AutoMapper.Map(skeleton);
        // names can contradict themselves on unusual rigs (a hand-only rig: one bone guessed as both Neck and
        // UpperLegL). A bone with several roles keeps none - the shape analysis decides instead.
        foreach (var bone in map.RoleToBone.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToList())
            foreach (var (role, _) in bone.ToList())
                map.RoleToBone.Remove(role);
        if (map.Confidence < 0.5f)
            problems.Add($"Bone roles were only partly recognised ({map.Confidence:P0}). Check the bone mapping.");
        TargetRig rig;
        try { rig = TargetRig.FromSkeleton(skeleton, map); }
        catch (ArgumentException)
        {
            // never fail to open a model over its bone names: fall back to the skeleton's shape alone
            map = new MappingResult("shape", map.Source);
            rig = TargetRig.FromSkeleton(skeleton, map);
        }
        return new MotionRig(skeleton, rig, map, unitsPerCm, problems);
    }

    /// <summary>A complete humanoid found from the bone names (hips, both legs, both arms, head).</summary>
    static bool IsRoleHumanoid(MappingResult map)
    {
        var r = map.RoleToBone;
        return r.ContainsKey(BoneRole.Hips) && r.ContainsKey(BoneRole.Head)
            && r.ContainsKey(BoneRole.UpperLegL) && r.ContainsKey(BoneRole.LowerLegL) && r.ContainsKey(BoneRole.FootL)
            && r.ContainsKey(BoneRole.UpperLegR) && r.ContainsKey(BoneRole.LowerLegR) && r.ContainsKey(BoneRole.FootR)
            && r.ContainsKey(BoneRole.UpperArmL) && r.ContainsKey(BoneRole.UpperArmR);
    }

    /// <summary>A leg chain as a foot chain (upper leg, knee, ankle, toe); null when it is too short.</summary>
    static FootChain? FootOf(RigLimb limb)
    {
        var c = limb.Chain;
        if (c.Count < 3) return null;
        var hasToe = c.Count >= 4;
        var start = hasToe ? c.Count - 4 : c.Count - 3;
        return new FootChain { Hip = c[start], Knee = c[start + 1], Ankle = c[start + 2], Toe = hasToe ? c[start + 3] : null };
    }

    BodyRegion RegionFromShape(int bone)
    {
        var a = Analysis;
        var left = a.Side[bone] == BoneSide.Left;
        return a.Part[bone] switch
        {
            RigPart.Root or RigPart.Hips => BodyRegion.Root,
            RigPart.Spine or RigPart.Neck => BodyRegion.Spine,
            RigPart.Head or RigPart.HeadPart => BodyRegion.Head,
            RigPart.Tail or RigPart.Fin => BodyRegion.Tail,
            RigPart.Arm or RigPart.Wing or RigPart.FrontLeg => left ? BodyRegion.ArmL : BodyRegion.ArmR,
            RigPart.Leg => left ? BodyRegion.LegL : BodyRegion.LegR,
            RigPart.Digit => DigitRegion(bone, left),
            _ => BodyRegion.Other,
        };
    }

    BodyRegion DigitRegion(int bone, bool left)
    {
        var limb = Analysis.Limbs.FirstOrDefault(l => l.Digits.Contains(bone));
        if (limb?.Kind == LimbKind.Leg) return left ? BodyRegion.LegL : BodyRegion.LegR;
        return left ? BodyRegion.HandL : BodyRegion.HandR;
    }

    /// <summary>Body region of a bone (inherited from the nearest mapped ancestor).</summary>
    public BodyRegion RegionOf(int bone) => _regions[bone];

    /// <summary>
    /// True for bones that carry authored motion (not IK helpers, not constraint-driven helpers, not face
    /// bones). These are the bones a motion generator animates; the rest are derived afterwards.
    /// </summary>
    public bool IsMotionBone(int bone) => _motionBones[bone];

    /// <summary>
    /// The bone with a humanoid role. For rigs read from their shape, roles come from the shape's labels (and only
    /// for humanoids): bone names that look like roles aren't trusted on a creature.
    /// </summary>
    public int? Bone(BoneRole role)
    {
        if (HasNamedRoles) return Map.RoleToBone.TryGetValue(role, out var b) ? b : null;
        if (!Analysis.IsHumanoid || !ShapeRoleLabels.TryGetValue(role, out var label)) return null;
        var bone = Array.IndexOf(Analysis.Label, label);
        return bone >= 0 && Analysis.InBody[bone] ? bone : null;
    }

    static readonly Dictionary<BoneRole, string> ShapeRoleLabels = new()
    {
        [BoneRole.Hips] = "Hips", [BoneRole.Neck] = "Neck", [BoneRole.Head] = "Head",
        [BoneRole.ClavicleL] = "Left Shoulder", [BoneRole.UpperArmL] = "Left Upper Arm", [BoneRole.LowerArmL] = "Left Forearm", [BoneRole.HandL] = "Left Hand",
        [BoneRole.ClavicleR] = "Right Shoulder", [BoneRole.UpperArmR] = "Right Upper Arm", [BoneRole.LowerArmR] = "Right Forearm", [BoneRole.HandR] = "Right Hand",
        [BoneRole.UpperLegL] = "Left Thigh", [BoneRole.LowerLegL] = "Left Shin", [BoneRole.FootL] = "Left Foot", [BoneRole.ToeL] = "Left Toe",
        [BoneRole.UpperLegR] = "Right Thigh", [BoneRole.LowerLegR] = "Right Shin", [BoneRole.FootR] = "Right Foot", [BoneRole.ToeR] = "Right Toe",
    };

    /// <summary>All bones in the subtree under <paramref name="bone"/>, including it.</summary>
    public IEnumerable<int> Descendants(int bone)
    {
        yield return bone;
        for (var i = bone + 1; i < Skeleton.Count; i++)
            if (IsAncestor(bone, i)) yield return i;
    }

    public bool IsAncestor(int ancestor, int bone)
    {
        for (var p = Skeleton[bone].ParentIndex; p >= 0; p = Skeleton[p].ParentIndex)
            if (p == ancestor) return true;
        return false;
    }

    /// <summary>Converts a distance in centimeters to skeleton units.</summary>
    public float Cm(float centimeters) => centimeters * UnitsPerCm;

    /// <summary>Foot plant thresholds rescaled from cm to skeleton units.</summary>
    public FootPlantOptions PlantOptions(float fps)
    {
        var options = new FootPlantOptions();
        options.SpeedThresholdCmPerSec *= UnitsPerCm;
        options.HeightThresholdCm *= UnitsPerCm;
        options.MinPlantFrames = Math.Max(2, (int)MathF.Ceiling(fps * 0.1f));
        return options;
    }

    BodyRegion ComputeRegion(int bone)
    {
        for (var b = bone; b >= 0; b = Skeleton[b].ParentIndex)
        {
            var role = Rig.RoleOf(b);
            if (role is null) continue;
            var region = RegionOfRole(role.Value);
            if (region != BodyRegion.Other) return region;
        }
        return bone == RootIndex ? BodyRegion.Root : BodyRegion.Other;
    }

    static BodyRegion RegionOfRole(BoneRole role)
    {
        var name = role.ToString();
        if (role == BoneRole.Hips) return BodyRegion.Root;
        if (name.StartsWith("Spine", StringComparison.Ordinal)) return BodyRegion.Spine;
        if (role is BoneRole.Neck or BoneRole.Head) return BodyRegion.Head;
        var left = name.EndsWith('L');
        if (name.StartsWith("Clavicle", StringComparison.Ordinal) || name.StartsWith("UpperArm", StringComparison.Ordinal)
            || name.StartsWith("LowerArm", StringComparison.Ordinal))
            return left ? BodyRegion.ArmL : BodyRegion.ArmR;
        if (name.StartsWith("Hand", StringComparison.Ordinal) || name.StartsWith("Thumb", StringComparison.Ordinal)
            || name.StartsWith("Index", StringComparison.Ordinal) || name.StartsWith("Middle", StringComparison.Ordinal)
            || name.StartsWith("Ring", StringComparison.Ordinal) || name.StartsWith("Pinky", StringComparison.Ordinal))
            return left ? BodyRegion.HandL : BodyRegion.HandR;
        if (name.StartsWith("UpperLeg", StringComparison.Ordinal) || name.StartsWith("LowerLeg", StringComparison.Ordinal)
            || name.StartsWith("Foot", StringComparison.Ordinal) || name.StartsWith("Toe", StringComparison.Ordinal))
            return left ? BodyRegion.LegL : BodyRegion.LegR;
        return BodyRegion.Other;
    }

    static FootChain? Chain(MappingResult map, BoneRole hip, BoneRole knee, BoneRole ankle, BoneRole toe)
    {
        if (!map.RoleToBone.TryGetValue(hip, out var h) || !map.RoleToBone.TryGetValue(knee, out var k)
            || !map.RoleToBone.TryGetValue(ankle, out var a))
            return null;
        return new FootChain { Hip = h, Knee = k, Ankle = a, Toe = map.RoleToBone.TryGetValue(toe, out var t) ? t : null };
    }

    static int MotionRootOf(Skeleton skeleton, TargetRig rig, int hips)
    {
        var root = hips;
        for (var p = skeleton[hips].ParentIndex; p >= 0; p = skeleton[p].ParentIndex)
            if (rig.ClassOf(p) == BoneClass.Animated) root = p;
        return root;
    }

    static int FirstRoot(Skeleton skeleton)
    {
        for (var i = 0; i < skeleton.Count; i++) if (skeleton[i].ParentIndex < 0) return i;
        return 0;
    }

    internal static Vector3 SnapToAxis(Vector3 v)
    {
        var ax = MathF.Abs(v.X); var ay = MathF.Abs(v.Y); var az = MathF.Abs(v.Z);
        if (ax >= ay && ax >= az) return new Vector3(MathF.Sign(v.X), 0, 0);
        if (ay >= az) return new Vector3(0, MathF.Sign(v.Y), 0);
        return new Vector3(0, 0, MathF.Sign(v.Z) == 0 ? 1 : MathF.Sign(v.Z));
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

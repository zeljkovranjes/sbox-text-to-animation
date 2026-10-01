#nullable enable annotations

using System.Globalization;
using System.Numerics;
using TextToAnimation.Animation;
using TextToAnimation.Formats;
using TextToAnimation.Maths;
using TextToAnimation.Processing;
using RigClip = TextToAnimation.Rig.Clip;

namespace TextToAnimation.Vmdl;

using Vector3 = System.Numerics.Vector3; // s&box compat (see Code/TextToAnimation/Assembly.cs)

/// <summary>A file the save writes next to the model.</summary>
public sealed record OutputFile(string AssetPath, string Content);

/// <summary>The planned result of saving clips into a vmdl: the new vmdl text and every DMX it references.</summary>
public sealed class VmdlSavePlan
{
    public required string VmdlText { get; init; }
    public required string OriginalVmdlText { get; init; }
    public List<OutputFile> Files { get; } = new();
    /// <summary>Sequence names this save writes (for compile verification).</summary>
    public List<string> Sequences { get; } = new();
    /// <summary>Expected frames per sequence (engine space, for round-trip verification).</summary>
    public Dictionary<string, List<XForm[]>> Expected { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = new();
}

/// <summary>Options for one clip being saved.</summary>
public sealed class ClipSaveRequest
{
    public required AnimClip Clip { get; init; }
    /// <summary>Final frames (keys applied).</summary>
    public required List<XForm[]> Frames { get; init; }
    /// <summary>Sequence to write. Must be new unless <see cref="ReplaceExisting"/> is set.</summary>
    public required string SequenceName { get; init; }
    /// <summary>Overwrite the AnimFile of the same name in place, keeping its other settings.</summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>
/// Turns workspace clips into DMX files and AnimFile entries in an existing vmdl. Frames are engine space
/// (inches, Z-up); the DMX is written in the vmdl's source units (dividing by its ScaleAndMirror scale) and
/// with the root yaw compensation the model compiler applies to Z-up animation files.
/// </summary>
public static class ClipVmdlWriter
{
    /// <summary>Folder (relative to the vmdl's folder) that holds the DMX files this library writes.</summary>
    public const string OutputFolderName = "text_to_animation";

    /// <summary>
    /// Yaw (degrees about +Z) pre-applied to root bones in written DMX. resourcecompiler turns Z-up animation
    /// files by -90° relative to the model's bind (measured in humanoid-retargeter, "PrepareDmxFrames").
    /// </summary>
    public static float RootYawCompensationDegrees { get; set; } = 90f;

    public static VmdlSavePlan Plan(string vmdlText, string vmdlAssetPath, MotionRig rig, IReadOnlyList<ClipSaveRequest> requests)
    {
        var doc = Kv3.Parse(vmdlText);
        if (doc.Root is not KvObject root || root.GetOrNull("rootNode") is not KvObject rootNode)
            throw new FormatException("The vmdl has no rootNode.");

        var scale = ModelScale(rootNode, out var mirrored);
        var plan = new VmdlSavePlan { VmdlText = vmdlText, OriginalVmdlText = vmdlText };
        if (mirrored) plan.Notes.Add("The model mirrors itself (ScaleAndMirror); saved animations may appear mirrored.");

        var folder = AssetFolder(vmdlAssetPath);
        var modelName = FileStem(vmdlAssetPath);
        var dmxFolder = Combine(folder, OutputFolderName + "/" + AnimClip.SanitizeSequenceName(modelName));

        var existing = ExistingAnimFiles(rootNode);
        var motionRoot = Walk(rootNode).FirstOrDefault(n => n.GetString("_class") == "AnimationList")?.GetString("default_root_bone_name");
        if (string.IsNullOrEmpty(motionRoot)) motionRoot = rig.Skeleton[rig.RootIndex].Name;
        var newEntries = new List<AnimEntry>();
        var replaced = new List<(string Name, AnimEntry Entry)>();

        foreach (var request in requests)
        {
            var clip = request.Clip;
            var seq = request.SequenceName;
            if (request.ReplaceExisting && !existing.ContainsKey(seq))
                throw new InvalidOperationException($"\"{seq}\" isn't defined in this vmdl, so it can't be replaced.");
            if (!request.ReplaceExisting && existing.ContainsKey(seq))
                throw new InvalidOperationException($"The vmdl already has a sequence named \"{seq}\". Rename the clip or choose Replace.");

            var frames = request.Frames;
            var dmxPath = $"{dmxFolder}/{seq}.dmx";
            plan.Files.Add(new OutputFile(dmxPath, BuildDmx(rig, frames, clip.Fps, clip.Looping, seq, scale)));
            plan.Expected[seq] = frames;
            plan.Sequences.Add(seq);

            var entry = new AnimEntry
            {
                Name = seq,
                SourceFilename = dmxPath,
                Looping = clip.Looping,
                ExtractMotion = clip.Export.RootMotion == ClipRootMotion.Extract,
                Events = clip.Events.Where(e => clip.Export.Footsteps || e.EventClass != FootstepEvents.FootstepEventClass).Select(ToEvent).ToList(),
            };
            if (request.ReplaceExisting) replaced.Add((seq, entry)); else newEntries.Add(entry);

            if (clip.Export.AdditiveVariant)
            {
                var deltaName = Unique(seq + "_delta", existing.Keys, plan.Sequences);
                var reference = Math.Clamp(clip.Export.AdditiveReferenceFrame, 0, Math.Max(0, frames.Count - 1));
                newEntries.Add(new AnimEntry
                {
                    Name = deltaName, SourceFilename = dmxPath, Looping = clip.Looping,
                    SubtractAnimName = seq, SubtractFrame = reference,
                });
                plan.Sequences.Add(deltaName);
            }

            if (clip.Export.MirroredVariant)
            {
                var mirroredFrames = ClipCleanup.MirrorFrames(frames, rig, out var error);
                if (mirroredFrames is null)
                    plan.Notes.Add($"No mirrored copy of \"{seq}\": {error}");
                else
                {
                    var mirrorName = Unique(seq + "_mirror", existing.Keys, plan.Sequences);
                    var mirrorPath = $"{dmxFolder}/{mirrorName}.dmx";
                    plan.Files.Add(new OutputFile(mirrorPath, BuildDmx(rig, mirroredFrames, clip.Fps, clip.Looping, mirrorName, scale)));
                    newEntries.Add(new AnimEntry
                    {
                        Name = mirrorName, SourceFilename = mirrorPath, Looping = clip.Looping,
                        ExtractMotion = entry.ExtractMotion,
                        Events = clip.Events.Select(ToEvent).Select(MirrorFoot).ToList(),
                    });
                    plan.Sequences.Add(mirrorName);
                    plan.Expected[mirrorName] = mirroredFrames;
                }
            }
        }

        // Replace in place: keep the user's node (fades, activities, custom children), swap the data.
        foreach (var (name, entry) in replaced)
            ReplaceAnimFile(existing[name], entry, motionRoot!);

        var text = Kv3.Serialize(doc);
        if (newEntries.Count > 0)
        {
            text = VmdlAugmenter.Augment(text, newEntries, out _, new AugmentOptions
            {
                DefaultRootBone = rig.Skeleton[rig.RootIndex].Name,
                DmxFolderRelative = dmxFolder,
            });
        }
        return new VmdlSavePlan { VmdlText = text, OriginalVmdlText = vmdlText }.CopyFrom(plan);
    }

    /// <summary>A DMX of the frames in the model's source units with root yaw compensation.</summary>
    public static string BuildDmx(MotionRig rig, IReadOnlyList<XForm[]> frames, float fps, bool looping, string name, float modelScale)
    {
        var inv = modelScale > 0f ? 1f / modelScale : 1f;
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, RootYawCompensationDegrees * MathF.PI / 180f);
        var skeleton = rig.Skeleton;
        XForm Convert(XForm x, bool isRoot)
        {
            var pos = x.Pos * inv;
            var rot = x.Rot;
            if (isRoot) { pos = Vector3.Transform(pos, yaw); rot = MathQ.Normalize(yaw * rot); }
            return new XForm(pos, rot);
        }
        var defs = skeleton.Bones.Select(b => new Rig.BoneDefinition(b.Name, b.ParentIndex >= 0 ? skeleton[b.ParentIndex].Name : null,
            Convert(b.RestLocal, b.ParentIndex < 0))).ToList();
        var dmxSkeleton = Rig.Skeleton.Create(defs);
        // Skeleton.Create keeps topological order identical for an already sorted skeleton; map by name to be safe.
        var map = skeleton.Bones.Select(b => dmxSkeleton.IndexOf(b.Name)).ToArray();
        var outFrames = new List<XForm[]>(frames.Count);
        foreach (var frame in frames)
        {
            var o = new XForm[skeleton.Count];
            for (var b = 0; b < skeleton.Count; b++) o[map[b]] = Convert(frame[b], skeleton[b].ParentIndex < 0);
            outFrames.Add(o);
        }
        QuaternionContinuity.AlignFrames(outFrames);
        return DmxWriter.Write(dmxSkeleton, new RigClip(name, fps, looping, outFrames),
            new DmxWriteOptions { Name = name, SourceNote = "text-to-animation", UpAxisY = false });
    }

    /// <summary>The vmdl's ScaleAndMirror scale (1 when absent).</summary>
    public static float ModelScale(KvObject rootNode, out bool mirrored)
    {
        mirrored = false;
        var scale = 1f;
        foreach (var node in Walk(rootNode))
        {
            if (node.GetString("_class") != "ModelModifier_ScaleAndMirror") continue;
            if (node.GetOrNull("scale") is KvDouble d) scale *= (float)d.Value;
            else if (node.GetOrNull("scale") is KvLong l) scale *= l.Value;
            mirrored |= node.GetOrNull("mirror_x") is KvBool { Value: true } || node.GetOrNull("mirror_y") is KvBool { Value: true }
                || node.GetOrNull("mirror_z") is KvBool { Value: true };
        }
        return scale;
    }

    /// <summary>All AnimFile nodes by name (searching folders).</summary>
    public static Dictionary<string, KvObject> ExistingAnimFiles(KvObject rootNode)
    {
        var result = new Dictionary<string, KvObject>(StringComparer.Ordinal);
        foreach (var node in Walk(rootNode))
            if (node.GetString("_class") == "AnimFile" && node.GetString("name") is { Length: > 0 } n && !result.ContainsKey(n))
                result[n] = node;
        return result;
    }

    static IEnumerable<KvObject> Walk(KvObject node)
    {
        yield return node;
        if (node.GetOrNull("children") is KvArray children)
            foreach (var child in children.Items.OfType<KvObject>())
                foreach (var n in Walk(child)) yield return n;
    }

    static void ReplaceAnimFile(KvObject node, AnimEntry entry, string motionRoot)
    {
        node["source_filename"] = new KvString(entry.SourceFilename);
        node["take"] = new KvLong(0);
        node["start_frame"] = new KvLong(-1);
        node["end_frame"] = new KvLong(-1);
        node["framerate"] = new KvDouble(-1.0);
        node["reverse"] = new KvBool(false);
        node["looping"] = new KvBool(entry.Looping);
        node["delta"] = new KvBool(false);
        // children: keep everything except what this save owns (footsteps and root motion extraction)
        var children = node.GetOrNull("children") as KvArray ?? new KvArray();
        var built = VmdlWriter.BuildAnimFileNode(entry, motionRoot);
        var builtChildren = built.GetOrNull("children") as KvArray;
        var hasOwnFootsteps = entry.Events.Any(e => e.EventClass == FootstepEvents.FootstepEventClass);
        children.Items.RemoveAll(c => c is KvObject o
            && (o.GetString("_class") == "ExtractMotion"
                || (hasOwnFootsteps && o.GetString("_class") == "AnimEvent" && o.GetString("event_class") == FootstepEvents.FootstepEventClass)));
        if (builtChildren is not null)
        {
            foreach (var child in builtChildren.Items.OfType<KvObject>())
            {
                if (child.GetString("_class") is "ExtractMotion" or "AnimEvent")
                    children.Items.Add(child);
            }
        }
        node["children"] = children;
    }

    static AnimEventEntry ToEvent(ClipEvent e) => new()
    {
        EventClass = e.EventClass, Frame = e.Frame, Attachment = e.Attachment, Foot = e.Foot, Volume = e.Volume,
    };

    static AnimEventEntry MirrorFoot(AnimEventEntry e)
    {
        if (e.Foot is "0") { e.Foot = "1"; e.Attachment = e.Attachment?.Replace("_L", "_R", StringComparison.Ordinal); }
        else if (e.Foot is "1") { e.Foot = "0"; e.Attachment = e.Attachment?.Replace("_R", "_L", StringComparison.Ordinal); }
        return e;
    }

    static string Unique(string wanted, IEnumerable<string> a, IEnumerable<string> b)
    {
        var used = new HashSet<string>(a.Concat(b), StringComparer.OrdinalIgnoreCase);
        var name = wanted;
        for (var i = 2; used.Contains(name); i++) name = $"{wanted}_{i}";
        return name;
    }

    static string AssetFolder(string assetPath)
    {
        var p = assetPath.Replace('\\', '/');
        var slash = p.LastIndexOf('/');
        return slash < 0 ? "" : p.Substring(0, slash);
    }

    static string FileStem(string assetPath)
    {
        var p = assetPath.Replace('\\', '/');
        var name = p.Substring(p.LastIndexOf('/') + 1);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    static string Combine(string folder, string rest) => folder.Length == 0 ? rest : folder + "/" + rest;

    static VmdlSavePlan CopyFrom(this VmdlSavePlan target, VmdlSavePlan source)
    {
        target.Files.AddRange(source.Files);
        target.Sequences.AddRange(source.Sequences);
        foreach (var kv in source.Expected) target.Expected[kv.Key] = kv.Value;
        target.Notes.AddRange(source.Notes);
        return target;
    }
}

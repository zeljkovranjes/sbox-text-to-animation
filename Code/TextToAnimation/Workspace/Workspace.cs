#nullable enable annotations

using TextToAnimation.Animation;

namespace TextToAnimation.Workspace;

/// <summary>
/// The persistent animation workspace of one model (.vmdl): its clips and editor state. A workspace is
/// identified by a GUID; the model is matched by its normalized asset path, and the skeleton fingerprint
/// detects when the model's bones changed since the clips were made.
/// </summary>
public sealed class AnimationWorkspace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Project-relative asset path of the model, normalized (lowercase, forward slashes).</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Display name of the model.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Hash of the skeleton's bone names and hierarchy when the workspace was last opened.</summary>
    public string SkeletonFingerprint { get; set; } = "";

    /// <summary>
    /// When the model's source file was created, as last seen (null for workspaces from before this was recorded).
    /// A model deleted and made again at the same path has a newer one: it is a different model.
    /// </summary>
    public DateTime? ModelFileCreatedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public List<AnimClip> Clips { get; set; } = new();
    public Guid? ActiveClipId { get; set; }

    /// <summary>Workspace-wide default frame rate for new clips.</summary>
    public float DefaultFps { get; set; } = 30f;

    /// <summary>
    /// Root rotation (x, y, z, w) the model's DMX animations need to play as edited, measured on the first save
    /// (null until then: the Citizen's +90° yaw is tried first).
    /// </summary>
    public float[]? RootCompensation { get; set; }

    public AnimClip? Find(Guid id) => Clips.FirstOrDefault(c => c.Id == id);

    /// <summary>A clip name not used by another clip ("Walk", "Walk 2", ...).</summary>
    public string UniqueName(string wanted, Guid? except = null)
    {
        var baseName = string.IsNullOrWhiteSpace(wanted) ? "Animation" : wanted.Trim();
        var name = baseName;
        for (var i = 2; Clips.Any(c => c.Id != except && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} {i}";
        return name;
    }

    /// <summary>A sequence name not used by another clip of this workspace nor by <paramref name="taken"/>.</summary>
    public string UniqueSequenceName(string wanted, IEnumerable<string> taken, Guid? except = null)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        foreach (var c in Clips.Where(c => c.Id != except)) used.Add(c.EffectiveSequenceName);
        var baseName = AnimClip.SanitizeSequenceName(wanted);
        var name = baseName;
        for (var i = 2; used.Contains(name); i++) name = $"{baseName}_{i}";
        return name;
    }

    /// <summary>Normalizes an asset path for identity comparisons.</summary>
    public static string NormalizePath(string path)
        => (path ?? "").Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    /// <summary>Stable fingerprint of a skeleton (bone names + parent names, in order).</summary>
    public static string Fingerprint(Rig.Skeleton skeleton)
    {
        var text = string.Join("|", skeleton.Bones.Select(b => b.Name + "<" + (b.ParentIndex >= 0 ? skeleton[b.ParentIndex].Name : "")));
        // FNV-1a 64-bit: deterministic, no crypto dependency
        ulong hash = 14695981039346656037UL;
        foreach (var ch in text) { hash ^= ch; hash *= 1099511628211UL; }
        return hash.ToString("x16");
    }
}

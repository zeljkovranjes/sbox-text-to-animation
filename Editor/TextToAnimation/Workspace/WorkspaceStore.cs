using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TextToAnimation.Animation;
using TextToAnimation.Maths;
using TextToAnimation.Workspace;

namespace TextToAnimation.Editor.Workspace;

using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// Saves and loads workspaces on disk. Layout under <see cref="Root"/>:
/// <code>
/// index.json                      model path -> workspace id
/// &lt;id&gt;/workspace.json             workspace + clip metadata
/// &lt;id&gt;/clips/&lt;clipId&gt;.t2aclip    frame data (binary, bone names stored so skeleton changes remap safely)
/// </code>
/// Every write goes to a temporary file first and is then moved into place, so a crash never leaves a
/// half-written workspace. No Sandbox types: the store is unit tested outside the editor.
/// </summary>
public sealed class WorkspaceStore
{
    const uint ClipMagic = 0x43413254; // "T2AC"
    const int ClipVersion = 1;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Root { get; }

    public WorkspaceStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    string IndexPath => Path.Combine(Root, "index.json");
    string Dir(Guid id) => Path.Combine(Root, id.ToString("N"));
    string ManifestPath(Guid id) => Path.Combine(Dir(id), "workspace.json");
    string ClipPath(Guid id, Guid clip) => Path.Combine(Dir(id), "clips", clip.ToString("N") + ".t2aclip");

    // ------------------------------------------------------------------ index

    sealed class IndexDto { public Dictionary<string, Guid> Models { get; set; } = new(); }

    IndexDto ReadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<IndexDto>(File.ReadAllText(IndexPath), Json) ?? new IndexDto();
        }
        catch (JsonException) { /* rebuilt below */ }
        // rebuild from the workspace folders (index lost or corrupt)
        var index = new IndexDto();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var manifest = Path.Combine(dir, "workspace.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<WorkspaceDto>(File.ReadAllText(manifest), Json);
                if (dto is not null && dto.ModelPath.Length > 0) index.Models[dto.ModelPath] = dto.Id;
            }
            catch (JsonException) { }
        }
        return index;
    }

    void WriteIndex(IndexDto index) => AtomicWrite(IndexPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(index, Json)));

    /// <summary>The workspace id registered for a model, if any.</summary>
    public Guid? FindWorkspaceFor(string modelPath)
        => ReadIndex().Models.TryGetValue(AnimationWorkspace.NormalizePath(modelPath), out var id) && File.Exists(ManifestPath(id)) ? id : null;

    /// <summary>
    /// Stops using a model's workspace for that model path (a new model now lives there); its files stay on disk.
    /// </summary>
    public void Forget(string modelPath)
    {
        var index = ReadIndex();
        if (index.Models.Remove(AnimationWorkspace.NormalizePath(modelPath))) WriteIndex(index);
    }

    /// <summary>All known workspaces: (model path, id).</summary>
    public IReadOnlyList<(string ModelPath, Guid Id)> List()
        => ReadIndex().Models.Where(kv => File.Exists(ManifestPath(kv.Value))).Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key).ToList();

    // ------------------------------------------------------------------ workspace

    sealed class WorkspaceDto
    {
        public int Version { get; set; } = 1;
        public Guid Id { get; set; }
        public string ModelPath { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string SkeletonFingerprint { get; set; } = "";
        public DateTime? ModelFileCreatedUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public Guid? ActiveClipId { get; set; }
        public float DefaultFps { get; set; } = 30f;
        public float[] RootCompensation { get; set; }
        public List<ClipDto> Clips { get; set; } = new();
    }

    sealed class ClipDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public float Fps { get; set; }
        public bool Looping { get; set; }
        public List<ClipEvent> Events { get; set; } = new();
        public List<int> PinnedFrames { get; set; } = new();
        public List<string> LockedBones { get; set; } = new();
        public int KeyFalloff { get; set; } = 8;
        public List<KeyDto> Keys { get; set; } = new();
        public ClipOrigin Origin { get; set; }
        public string SourceSequence { get; set; }
        public GenerationRecord Generation { get; set; }
        public ClipExportSettings Export { get; set; } = new();
        public DateTime? SavedUtc { get; set; }
    }

    sealed class KeyDto
    {
        public string Bone { get; set; } = "";
        public int Frame { get; set; }
        public float[] Pos { get; set; } = new float[3];
        public float[] Rot { get; set; } = new float[4];
    }

    /// <summary>Saves the workspace manifest and every clip whose data changed since the last save.</summary>
    public void Save(AnimationWorkspace ws, Rig.Skeleton skeleton, IEnumerable<AnimClip> changedClips = null)
    {
        ws.ModifiedUtc = DateTime.UtcNow;
        ws.ModelPath = AnimationWorkspace.NormalizePath(ws.ModelPath);
        Directory.CreateDirectory(Path.Combine(Dir(ws.Id), "clips"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double Lap() { var ms = watch.Elapsed.TotalMilliseconds; watch.Restart(); return ms; }
        var toWrite = changedClips?.ToList() ?? ws.Clips;
        var frames = toWrite.Select(clip => (clip, bytes: SerializeFrames(clip, skeleton))).ToList();
        var serialize = Lap();
        foreach (var (clip, bytes) in frames)
            AtomicWrite(ClipPath(ws.Id, clip.Id), bytes);
        var write = Lap();

        var dto = new WorkspaceDto
        {
            Id = ws.Id, ModelPath = ws.ModelPath, ModelName = ws.ModelName, SkeletonFingerprint = ws.SkeletonFingerprint, ModelFileCreatedUtc = ws.ModelFileCreatedUtc,
            CreatedUtc = ws.CreatedUtc, ModifiedUtc = ws.ModifiedUtc, ActiveClipId = ws.ActiveClipId, DefaultFps = ws.DefaultFps, RootCompensation = ws.RootCompensation,
            Clips = ws.Clips.Select(ToDto).ToList(),
        };
        AtomicWrite(ManifestPath(ws.Id), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto, Json)));

        // remove clip files of deleted clips
        var live = ws.Clips.Select(c => c.Id.ToString("N") + ".t2aclip").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(Path.Combine(Dir(ws.Id), "clips"), "*.t2aclip"))
            if (!live.Contains(Path.GetFileName(file))) File.Delete(file);

        var manifest = Lap();
        var index = ReadIndex();
        index.Models[ws.ModelPath] = ws.Id;
        WriteIndex(index);
        LastSaveTimings = $"frames {serialize:0.0} ms, clip files {write:0.0}, manifest {manifest:0.0}, index {Lap():0.0}";
    }

    /// <summary>Where the last save's time went (for the editor's frame probe).</summary>
    public static string LastSaveTimings { get; private set; } = "";

    /// <summary>Loads a workspace; clip frames are remapped by bone name onto <paramref name="skeleton"/>.</summary>
    public AnimationWorkspace Load(Guid id, Rig.Skeleton skeleton, List<string> warnings)
    {
        var dto = JsonSerializer.Deserialize<WorkspaceDto>(File.ReadAllText(ManifestPath(id)), Json)
            ?? throw new InvalidDataException("The workspace file is empty.");
        var ws = new AnimationWorkspace
        {
            Id = dto.Id, ModelPath = dto.ModelPath, ModelName = dto.ModelName, SkeletonFingerprint = dto.SkeletonFingerprint,
            ModelFileCreatedUtc = dto.ModelFileCreatedUtc, CreatedUtc = dto.CreatedUtc, ModifiedUtc = dto.ModifiedUtc, ActiveClipId = dto.ActiveClipId, DefaultFps = dto.DefaultFps, RootCompensation = dto.RootCompensation,
        };
        foreach (var c in dto.Clips)
        {
            var path = ClipPath(id, c.Id);
            if (!File.Exists(path)) { warnings.Add($"Clip \"{c.Name}\" has no frame data and was skipped."); continue; }
            try
            {
                var clip = FromDto(c);
                clip.Frames = DeserializeFrames(File.ReadAllBytes(path), skeleton, out var missing);
                if (missing > 0) warnings.Add($"Clip \"{c.Name}\": {missing} bones were added to the model since it was made; they use the rest pose.");
                ws.Clips.Add(clip);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
            {
                warnings.Add($"Clip \"{c.Name}\" could not be read ({ex.Message}).");
            }
        }
        if (ws.ActiveClipId is { } active && ws.Find(active) is null) ws.ActiveClipId = ws.Clips.FirstOrDefault()?.Id;
        return ws;
    }

    /// <summary>Deletes a workspace from disk (its clips are gone; the model and saved sequences are untouched).</summary>
    public void Delete(Guid id)
    {
        if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), recursive: true);
        var index = ReadIndex();
        foreach (var key in index.Models.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) index.Models.Remove(key);
        WriteIndex(index);
    }

    /// <summary>
    /// Serializes a throwaway workspace with a generated clip, in memory: the JSON serializer builds its metadata for
    /// these types the first time (tens of ms, ~170 in the editor) - done while a model opens instead of in the save
    /// right after the first generation, where it showed as a hitch when the result appeared.
    /// </summary>
    public static void Warmup()
    {
        var clip = new AnimClip { Name = "warmup", Origin = ClipOrigin.Generated, Generation = new GenerationRecord { Mode = "TextToMotion", Prompts = new() { "warmup" } } };
        JsonSerializer.Serialize(new WorkspaceDto { Clips = new() { ToDto(clip) } }, Json);
        JsonSerializer.SerializeToUtf8Bytes(new List<PromptHistoryEntry> { new() { Prompt = "warmup", ClipIds = new() { Guid.Empty } } }, Json);
    }

    static ClipDto ToDto(AnimClip c) => new()
    {
        Id = c.Id, Name = c.Name, Fps = c.Fps, Looping = c.Looping,
        Events = c.Events.Select(e => e.Clone()).ToList(),
        PinnedFrames = c.PinnedFrames.ToList(),
        LockedBones = c.LockedBones.OrderBy(b => b, StringComparer.Ordinal).ToList(),
        KeyFalloff = c.Keys.FalloffFrames,
        Keys = c.Keys.KeyedBones.SelectMany(bone => c.Keys.KeyFrames(bone).Select(frame =>
        {
            c.Keys.TryGetKey(bone, frame, out var d);
            return new KeyDto { Bone = bone, Frame = frame, Pos = new[] { d.Pos.X, d.Pos.Y, d.Pos.Z }, Rot = new[] { d.Rot.X, d.Rot.Y, d.Rot.Z, d.Rot.W } };
        })).ToList(),
        Origin = c.Origin, SourceSequence = c.SourceSequence, Generation = c.Generation, Export = c.Export, SavedUtc = c.SavedUtc,
    };

    static AnimClip FromDto(ClipDto c)
    {
        var clip = new AnimClip
        {
            Id = c.Id, Name = c.Name, Fps = c.Fps > 0 ? c.Fps : 30f, Looping = c.Looping,
            Events = c.Events ?? new(), PinnedFrames = new SortedSet<int>(c.PinnedFrames ?? new()),
            LockedBones = new HashSet<string>(c.LockedBones ?? new(), StringComparer.Ordinal),
            Origin = c.Origin, SourceSequence = c.SourceSequence, Generation = c.Generation, Export = c.Export ?? new(), SavedUtc = c.SavedUtc,
        };
        clip.Keys.FalloffFrames = c.KeyFalloff;
        foreach (var k in c.Keys ?? new())
            clip.Keys.SetKey(k.Bone, k.Frame, new XForm(new Vector3(k.Pos[0], k.Pos[1], k.Pos[2]), new Quaternion(k.Rot[0], k.Rot[1], k.Rot[2], k.Rot[3])));
        return clip;
    }

    // ------------------------------------------------------------------ binary frames

    /// <summary>Binary frame block: magic, version, fps, bone names, frame count, 7 floats per bone per frame.</summary>
    public static byte[] SerializeFrames(AnimClip clip, Rig.Skeleton skeleton)
    {
        var names = skeleton.Bones.Select(b => Encoding.UTF8.GetBytes(b.Name)).ToList();
        var size = 4 + 4 + 4 + 4 + names.Sum(n => 4 + n.Length) + 4 + clip.FrameCount * skeleton.Count * 28;
        var data = new byte[size];
        var o = 0;
        void I(int v) { BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o), v); o += 4; }
        void F(float v) { BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o), v); o += 4; }
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(o), ClipMagic); o += 4;
        I(ClipVersion);
        F(clip.Fps);
        I(skeleton.Count);
        foreach (var n in names) { I(n.Length); n.CopyTo(data, o); o += n.Length; }
        I(clip.FrameCount);
        foreach (var frame in clip.Frames)
        {
            if (frame.Length != skeleton.Count) throw new ArgumentException("Frame bone count doesn't match the skeleton.");
            foreach (var x in frame) { F(x.Pos.X); F(x.Pos.Y); F(x.Pos.Z); F(x.Rot.X); F(x.Rot.Y); F(x.Rot.Z); F(x.Rot.W); }
        }
        return data;
    }

    /// <summary>Reads frames, mapping stored bones onto <paramref name="skeleton"/> by name (unknown bones get the rest pose).</summary>
    public static List<XForm[]> DeserializeFrames(byte[] data, Rig.Skeleton skeleton, out int missingBones)
    {
        var o = 0;
        int I() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o)); o += 4; return v; }
        float F() { var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o)); o += 4; return v; }
        void Need(int n) { if (o + n > data.Length) throw new EndOfStreamException("Clip file is truncated."); }
        Need(4);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o)) != ClipMagic) throw new InvalidDataException("Not a clip file.");
        o += 4;
        var version = I();
        if (version != ClipVersion) throw new InvalidDataException($"Unsupported clip version {version}.");
        Need(4); F(); // fps (kept in the manifest)
        var count = I();
        if (count <= 0 || count > 10000) throw new InvalidDataException("Bad bone count.");
        var map = new int[count];
        var found = new bool[skeleton.Count];
        for (var i = 0; i < count; i++)
        {
            var len = I();
            Need(len);
            var name = Encoding.UTF8.GetString(data, o, len); o += len;
            map[i] = skeleton.IndexOf(name);
            if (map[i] >= 0) found[map[i]] = true;
        }
        missingBones = found.Count(f => !f);
        var frames = I();
        if (frames < 0) throw new InvalidDataException("Bad frame count.");
        Need(frames * count * 28);
        var result = new List<XForm[]>(frames);
        for (var f = 0; f < frames; f++)
        {
            var frame = new XForm[skeleton.Count];
            for (var b = 0; b < skeleton.Count; b++) frame[b] = skeleton[b].RestLocal;
            for (var i = 0; i < count; i++)
            {
                var x = new XForm(new Vector3(F(), F(), F()), new Quaternion(F(), F(), F(), F()));
                if (map[i] >= 0) frame[map[i]] = x;
            }
            result.Add(frame);
        }
        return result;
    }

    // ------------------------------------------------------------------ prompt history

    /// <summary>Most entries kept per workspace (oldest dropped first).</summary>
    public const int MaxPromptHistory = 200;

    string HistoryPath(Guid id) => Path.Combine(Dir(id), "prompts.json");

    /// <summary>The workspace's prompt history, newest first; null when it was never written.</summary>
    public List<PromptHistoryEntry> LoadPromptHistory(Guid id)
    {
        try
        {
            var path = HistoryPath(id);
            if (!File.Exists(path)) return null;
            var list = JsonSerializer.Deserialize<List<PromptHistoryEntry>>(File.ReadAllText(path), Json) ?? new List<PromptHistoryEntry>();
            return list.Where(e => !string.IsNullOrWhiteSpace(e.Prompt)).OrderByDescending(e => e.CreatedUtc).ToList();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new List<PromptHistoryEntry>(); // unreadable: start a fresh history rather than failing
        }
    }

    /// <summary>Writes the history (newest first, capped at <see cref="MaxPromptHistory"/>).</summary>
    public void SavePromptHistory(Guid id, IEnumerable<PromptHistoryEntry> entries)
    {
        var list = entries.OrderByDescending(e => e.CreatedUtc).Take(MaxPromptHistory).ToList();
        AtomicWrite(HistoryPath(id), JsonSerializer.SerializeToUtf8Bytes(list, Json));
    }

    static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}

/// <summary>One prompt the user sent, and what it made.</summary>
public sealed class PromptHistoryEntry
{
    public string Prompt { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<Guid> ClipIds { get; set; } = new();
    public string ClipName { get; set; } = "";
    public int Seed { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

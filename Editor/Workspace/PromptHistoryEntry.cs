using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Workspace;

namespace TextToAnimation.EditorTools.Workspace;

using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

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

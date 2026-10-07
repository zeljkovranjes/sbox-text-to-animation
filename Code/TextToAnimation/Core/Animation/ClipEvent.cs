#nullable enable annotations

using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

/// <summary>An animation event on the clip's frame grid (footsteps and user events).</summary>
public sealed class ClipEvent
{
    public string EventClass { get; set; } = "";
    public int Frame { get; set; }
    public string? Attachment { get; set; }
    public string? Foot { get; set; }
    public double? Volume { get; set; }
    /// <summary>True when the event was generated automatically (footsteps) and may be regenerated.</summary>
    public bool Automatic { get; set; }
    public ClipEvent Clone() => new()
    {
        EventClass = EventClass, Frame = Frame, Attachment = Attachment, Foot = Foot, Volume = Volume, Automatic = Automatic,
    };
}

#nullable enable annotations

using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;

namespace TextToAnimation.Core.Animation;

/// <summary>The settings that produced a generated clip, kept so it can be regenerated or varied.</summary>
public sealed class GenerationRecord
{
    public string Mode { get; set; } = "";
    public List<string> Prompts { get; set; } = new();
    public int Seed { get; set; }
    public float DurationSeconds { get; set; }
    public float Guidance { get; set; }
    public string Generator { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public GenerationRecord Clone() => new()
    {
        Mode = Mode, Prompts = Prompts.ToList(), Seed = Seed, DurationSeconds = DurationSeconds,
        Guidance = Guidance, Generator = Generator, CreatedUtc = CreatedUtc,
    };
}

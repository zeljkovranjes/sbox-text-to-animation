#nullable enable annotations

using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;

namespace TextToAnimation.Core.Generation;

/// <summary>
/// A text-to-motion model. The editor talks only to this interface, so other models can be added
/// later without touching the UI or the workspace code.
/// </summary>
public interface IMotionGenerator
{
    /// <summary>Display name ("UniMate").</summary>
    string Name { get; }

    GeneratorCapabilities Capabilities { get; }

    /// <summary>Checks a rig can be animated and returns problems (empty = fine).</summary>
    IReadOnlyList<string> Validate(MotionRig rig);

    /// <summary>The family <see cref="RigFamily.Auto"/> resolves to for this rig (shown in the UI as the default).</summary>
    RigFamily DetectFamily(MotionRig rig);

    /// <summary>Runs on a worker thread; must honour cancellation and never touch engine objects.</summary>
    Task<IReadOnlyList<GeneratedMotion>> GenerateAsync(MotionRig rig, GenerationRequest request,
        Action<GenerationProgress>? progress, CancellationToken token);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TextToAnimation.EditorTools.Inference.Onnx;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>Sampling settings.</summary>
public sealed class SampleSettings
{
	public int Steps { get; init; } = 24;
	public float Guidance { get; init; } = 3f;
	public Integrator Method { get; init; } = Integrator.Euler;
	/// <summary>
	/// Time-grid shift (1 = uniform). Above 1 the steps crowd towards the noisy start of the flow, where the
	/// velocity changes fastest: t' = s t / (1 + (s - 1) t).
	/// </summary>
	public float TimeShift { get; init; } = 1f;
	/// <summary>Start time of the flow (0 = pure noise; &gt;0 starts from a noised copy of <see cref="Known"/>: variations).</summary>
	public float StartTime { get; init; }
	/// <summary>Known normalised motion (J,12,T) for constraints / variations.</summary>
	public float[] Known { get; init; }
	/// <summary>(J,12,T) mask of values held to <see cref="Known"/> (replacement sampling).</summary>
	public bool[] Keep { get; init; }
	/// <summary>Dopri5 tolerances (upstream's sample_ode defaults).</summary>
	public double RelativeTolerance { get; init; } = 1e-3;
	public double AbsoluteTolerance { get; init; } = 1e-6;
}

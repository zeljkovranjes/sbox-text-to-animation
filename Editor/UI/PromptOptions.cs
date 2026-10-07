using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>The settings behind the prompt. Simple users never need to touch them.</summary>
public sealed class PromptOptions
{
	public float Seconds { get; set; } = 4f;
	public int Takes { get; set; } = 1;
	public int Steps { get; set; } = 24;
	public float Guidance { get; set; } = 3f;
	/// <summary>Null = a new random seed per generation.</summary>
	public int? Seed { get; set; }
	public float VariationStrength { get; set; } = 0.5f;
	public ChangeScope Scope { get; set; } = ChangeScope.WholeBody;
	/// <summary>Smooth the generated motion and lock planted feet (off = the model's raw output).</summary>
	public bool CleanUp { get; set; } = true;
}

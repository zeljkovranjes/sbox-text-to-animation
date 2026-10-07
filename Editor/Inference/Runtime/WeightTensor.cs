using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TextToAnimation.EditorTools.Inference.Runtime;

/// <summary>A named fp32 weight tensor.</summary>
public sealed record WeightTensor( string Name, int[] Shape, float[] Data )
{
	public int Length => Data.Length;
}

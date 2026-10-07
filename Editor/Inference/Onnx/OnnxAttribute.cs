using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>A node attribute.</summary>
public sealed class OnnxAttribute
{
	public string Name = "";
	public long? Int;
	public float? Float;
	public string String;
	public long[] Ints = Array.Empty<long>();
	public float[] Floats = Array.Empty<float>();
	public OnnxInitializer Tensor;
	public OnnxGraphProto Graph;
}

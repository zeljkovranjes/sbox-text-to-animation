using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

public sealed class OnnxValueInfo
{
	public string Name = "";
	public OnnxType Type;
	/// <summary>Dimensions; -1 for symbolic.</summary>
	public long[] Dims = Array.Empty<long>();
}

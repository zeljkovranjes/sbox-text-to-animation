using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>A constant tensor in the model file: inline bytes or a slice of an external data file.</summary>
public sealed class OnnxInitializer
{
	public string Name = "";
	public OnnxType Type;
	public long[] Dims = Array.Empty<long>();
	public byte[] Raw;
	public float[] FloatData;
	public long[] Int64Data;
	public int[] Int32Data;
	public string ExternalLocation;
	public long ExternalOffset;
	public long ExternalLength = -1;
}

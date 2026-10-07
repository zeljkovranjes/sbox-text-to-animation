using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

public sealed class OnnxNode
{
	public string Name = "";
	public string OpType = "";
	public string Domain = "";
	public string[] Inputs = Array.Empty<string>();
	public string[] Outputs = Array.Empty<string>();
	public Dictionary<string, OnnxAttribute> Attributes = new( StringComparer.Ordinal );

	public long GetInt( string name, long fallback ) => Attributes.TryGetValue( name, out var a ) && a.Int is long v ? v : fallback;
	public float GetFloat( string name, float fallback ) => Attributes.TryGetValue( name, out var a ) && a.Float is float v ? v : fallback;
	public string GetString( string name, string fallback ) => Attributes.TryGetValue( name, out var a ) && a.String is { } v ? v : fallback;
	public long[] GetInts( string name ) => Attributes.TryGetValue( name, out var a ) ? a.Ints : null;
	public override string ToString() => $"{OpType} '{Name}'";
}

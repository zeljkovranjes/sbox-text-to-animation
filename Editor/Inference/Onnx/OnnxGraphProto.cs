using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

public sealed class OnnxGraphProto
{
	public string Name = "";
	public List<OnnxNode> Nodes = new();
	public List<OnnxInitializer> Initializers = new();
	public List<OnnxValueInfo> Inputs = new();
	public List<OnnxValueInfo> Outputs = new();
}

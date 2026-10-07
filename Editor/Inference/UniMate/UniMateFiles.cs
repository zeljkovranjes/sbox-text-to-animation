using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TextToAnimation.EditorTools.Inference.Onnx;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>Files of an installed UniMate model.</summary>
public sealed record UniMateFiles( string Directory )
{
	public string WeightBlob => Path.Combine( Directory, $"unimate_v2_g{UniMateGraphBuilder.FormatVersion}.weights" );
	public string T5Encoder => Path.Combine( Directory, "t5_encoder.onnx" );
	public string T5Tokenizer => Path.Combine( Directory, "t5_tokenizer.json" );
	public string Graphs => Path.Combine( Directory, $"graphs_g{UniMateGraphBuilder.FormatVersion}" );
}

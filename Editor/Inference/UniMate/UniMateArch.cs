using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using TextToAnimation.EditorTools.Inference.Onnx;
using TextToAnimation.EditorTools.Inference.Runtime;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>Architecture constants of the UniMate graph_adaln denoiser.</summary>
public sealed record UniMateArch( int Layers, int Width = 512, int Heads = 8, int FfHidden = 1365, int MaxDepth = 19, int Features = 12, int TextWidth = 768 )
{
	public int HeadDim => Width / Heads;
	public static UniMateArch V2 { get; } = new( 10 );
	public static UniMateArch Mixamo { get; } = new( 6, MaxDepth: 7 );
}

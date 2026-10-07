using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TextToAnimation.EditorTools.Inference.Onnx;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>Conditioning tensors of one skeleton, computed once (the prepare graph's outputs).</summary>
public sealed class PreparedSkeleton
{
	public required UniMateSkeleton Skeleton { get; init; }
	public required Dictionary<string, Tensor> Tensors { get; init; }
}

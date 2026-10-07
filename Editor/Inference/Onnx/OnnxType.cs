using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>ONNX tensor element types used here (TensorProto.DataType).</summary>
public enum OnnxType { Undefined = 0, Float = 1, UInt8 = 2, Int8 = 3, Int32 = 6, Int64 = 7, String = 8, Bool = 9, Float16 = 10, Double = 11, BFloat16 = 16 }

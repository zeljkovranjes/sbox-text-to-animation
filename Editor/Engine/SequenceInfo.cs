using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Rig;
using TextToAnimation.Core.Vmdl;

namespace TextToAnimation.EditorTools.Engine;

using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

/// <summary>An animation sequence a model exposes, and where it comes from.</summary>
public sealed record SequenceInfo( string Name, bool DefinedInModel, string SourceFile, bool Looping, bool HasExtractMotion, bool IsDelta );

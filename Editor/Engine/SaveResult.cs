using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Processing;
using TextToAnimation.Core.Vmdl;

namespace TextToAnimation.EditorTools.Engine;

/// <summary>Outcome of a save: whether the model compiled and the clips play back as authored.</summary>
public sealed class SaveResult
{
	public bool Success { get; set; }
	public List<string> Errors { get; } = new();
	public List<string> Notes { get; } = new();
	public string VmdlPath { get; set; }
	public string BackupPath { get; set; }
	/// <summary>Largest difference (inches) between the compiled sequence and the clip, per sequence.</summary>
	public Dictionary<string, float> PlaybackError { get; } = new( StringComparer.Ordinal );
	public bool RolledBack { get; set; }
	/// <summary>Largest root rotation difference (degrees) between the compiled sequences and the clips.</summary>
	public float RootRotationError { get; set; }
	/// <summary>The root compensation this model needs, when the save measured a new one (store it with the workspace).</summary>
	public System.Numerics.Quaternion? LearnedRootCompensation { get; set; }
}

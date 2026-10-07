using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Engine;
using TextToAnimation.EditorTools.Workspace;
using TextToAnimation.Core.Maths;
using TextToAnimation.Core.Workspace;

namespace TextToAnimation.EditorTools.Session;

/// <summary>What changed, so panels only refresh what they show.</summary>
[Flags]
public enum SessionChange
{
	None = 0,
	Model = 1,
	ClipList = 2,
	ActiveClip = 4,
	ClipData = 8,
	Playhead = 16,
	Selection = 32,
	Undo = 64,
	Busy = 128,
}

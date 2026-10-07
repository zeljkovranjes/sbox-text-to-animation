using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.UI;

/// <summary>Which part of an existing animation a text change applies to.</summary>
public enum ChangeScope { WholeBody, UpperBody, LowerBody, Arms, SelectedBones, Unlocked }

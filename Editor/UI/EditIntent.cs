using System;
using System.Collections.Generic;
using System.Linq;
using TextToAnimation.Core.Animation;
using TextToAnimation.EditorTools.Session;
using TextToAnimation.Core.Generation;

namespace TextToAnimation.EditorTools.UI;

/// <summary>What a prompt does to the open animation.</summary>
public enum EditIntent { Change, FillBetween, Variations, New }

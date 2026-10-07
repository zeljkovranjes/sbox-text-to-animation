using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Generation;

namespace TextToAnimation.EditorTools.Generation;

/// <summary>Where the motion model is in its life cycle (drives the Generate panel's state).</summary>
public enum ModelState { NotInstalled, Downloading, Incomplete, Loading, Ready, Failed }

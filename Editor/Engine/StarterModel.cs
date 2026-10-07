using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TextToAnimation.EditorTools.Engine;

/// <summary>An s&amp;box character a new model can start from.</summary>
public sealed record StarterModel( string Title, string AssetPath, string Description );

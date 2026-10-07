#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TextToAnimation.Core.Vmdl;

/// <summary>A KV3 array.</summary>
public sealed class KvArray : KvValue
{
    /// <summary>The items, in order.</summary>
    public List<KvValue> Items { get; } = new();
}

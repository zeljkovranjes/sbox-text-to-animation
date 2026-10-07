#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TextToAnimation.Core.Vmdl;

/// <summary>A KV3 boolean.</summary>
public sealed class KvBool : KvValue
{
    /// <summary>The boolean value.</summary>
    public bool Value { get; }

    /// <summary>Creates a boolean value.</summary>
    public KvBool(bool value) => Value = value;
}

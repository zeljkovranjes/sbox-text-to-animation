#nullable enable annotations

using System.Text;
using System.Text.RegularExpressions;

namespace TextToAnimation.Rig;

/// <summary>
/// Raw bone name to the "clean" joint vocabulary UniMate was trained on ("mixamorig:LeftUpLeg" -> "Left Thigh",
/// "thigh.L" -> "Left Thigh", "Tail_05" -> "Tail"). A port of the generic and Mixamo paths of UniMate's
/// data_process/joint_annotation/names_clean_rule.py (clean_joint_name + post_process); the dataset-specific
/// maps (Japanese, spider, NPC, elk rigs) are left out. Names are only a hint: <see cref="RigAnalysis"/>
/// labels bones from geometry and uses these for bones it cannot classify.
/// </summary>
public static class JointNames
{
    static readonly Regex TrailingToken = new(@"[._](\d+|[LRlr]|x)$", RegexOptions.CultureInvariant);
    static readonly Regex SideToken = new(@"[._]([LR])(?=[._]|\d|$)", RegexOptions.CultureInvariant);
    static readonly Regex SingleSide = new(@"^[LR](?:[A-Z][a-z]|[A-Z]{0,2}_[A-Za-z])", RegexOptions.CultureInvariant);
    static readonly Regex FingerCode = new(@"^[Ff]inger([0-4])\d*(Nub)?$", RegexOptions.CultureInvariant);
    static readonly Regex FingerSeg = new(@"^[Ff]inger([1-5])(Metacarpal|Proximal|Medial|Distal|Tip)\d*$", RegexOptions.CultureInvariant);
    static readonly Regex ParenDecor = new(@"\s*\([^)]*\)", RegexOptions.CultureInvariant);
    static readonly Regex Namespace = new(@"^[A-Za-z][\w .-]*:", RegexOptions.CultureInvariant);
    static readonly Regex BipPrefix = new(@"^(?:BN_)?Bip\d+(?:[-_ ]+|(?=[LR][A-Z]))", RegexOptions.CultureInvariant);
    static readonly Regex Mixamorig = new(@"^mixamorig\d*[:_]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly string[] FingerCodeNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };

    /// <summary>The cleaned, post-processed joint name (never empty; "Bone" when nothing anatomical survives).</summary>
    public static string Clean(string raw) => PostProcess(CleanJointName(raw ?? ""));

    /// <summary>True when <paramref name="cleaned"/> (side word removed) is a word of the training vocabulary.</summary>
    public static bool IsKnown(string cleaned)
    {
        var (_, baseName) = SplitSide(cleaned);
        if (baseName.Length == 0 || baseName == "Bone") return false;
        if (baseName.EndsWith(" End", StringComparison.Ordinal)) baseName = baseName[..^4];
        return KnownWords.Contains(baseName);
    }

    /// <summary>("Left", "Thigh") from "Left Thigh"; side is "" for center names.</summary>
    public static (string Side, string Base) SplitSide(string cleaned)
    {
        if (cleaned.StartsWith("Left ", StringComparison.Ordinal)) return ("Left", cleaned[5..]);
        if (cleaned.StartsWith("Right ", StringComparison.Ordinal)) return ("Right", cleaned[6..]);
        return ("", cleaned);
    }

    static HashSet<string>? _known;
    static HashSet<string> KnownWords
    {
        get
        {
            if (_known is not null) return _known;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in Canonical.Values) if (v.Length > 0) set.Add(SplitSide(v).Base);
            foreach (var v in MixamoMap.Values) set.Add(SplitSide(v).Base);
            foreach (var v in SymmetricPairPriority) set.Add(v);
            set.Remove("Bone");
            set.Remove("End");
            return _known = set;
        }
    }

    static string CleanJointName(string raw)
    {
        if (raw.Trim().Length == 0) return raw;
        var stripped = ParenDecor.Replace(raw, "").Trim();
        if (stripped.Length > 0) raw = stripped;
        if (Regex.IsMatch(raw, @"^Bone\d+$")) return "Bone";
        if (Regex.IsMatch(raw.Trim('_'), @"^_?\d+$")) return raw;
        var m = Mixamorig.Match(raw);
        if (m.Success) return CleanPrefixed(raw, m.Value, MixamoMap);
        return CleanStandard(raw);
    }

    static string CleanPrefixed(string raw, string prefix, Dictionary<string, string> map)
    {
        var name = raw[prefix.Length..];
        var (side, rest) = ExtractSidePrefix(name);
        if (!map.TryGetValue(rest, out var result) && !map.TryGetValue(Regex.Replace(rest, @"\d+$", ""), out result))
        {
            result = SplitAndMapTokens(rest);
            if (result.Length == 0) result = rest;
        }
        return WithSide(side, result).Trim();
    }

    static string? Canon(string token)
    {
        if (Canonical.TryGetValue(token, out var value)) return value;
        if (token.Length > 0)
        {
            var cap = Capitalize(token);
            if (cap != token && Canonical.TryGetValue(cap, out value)) return value;
        }
        return null;
    }

    /// <summary>Python's str.capitalize(): first character upper, the rest lower.</summary>
    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    static (string Side, string Name) StripTrailingDecorations(string name)
    {
        var side = "";
        while (true)
        {
            var m = TrailingToken.Match(name);
            if (!m.Success) return (side, name);
            var tok = m.Groups[1].Value;
            if (tok is "L" or "l") side = "Left";
            else if (tok is "R" or "r") side = "Right";
            name = name[..m.Index];
        }
    }

    static (string Side, string Name) ExtractSidePrefix(string name)
    {
        var (side, baseName) = StripTrailingDecorations(name);
        name = baseName;
        if (side.Length > 0)
        {
            var cut = Regex.Replace(name, @"^(?:Left|Right)(?=[A-Z])|^(?:Left|Right)[_ ]|^[LR][-_]|^[lr]_", "");
            return (side, cut.Length > 0 ? cut : name);
        }
        if (Regex.IsMatch(name, @"^[RL][-_.]")) return (name[0] == 'L' ? "Left" : "Right", name[2..]);
        var m = Regex.Match(name, @"^([RL]) +");
        if (m.Success) return (m.Groups[1].Value == "L" ? "Left" : "Right", name[m.Length..]);
        m = Regex.Match(name, @"^(Left|Right)_", RegexOptions.IgnoreCase);
        if (m.Success) return (Capitalize(m.Groups[1].Value), name[m.Length..]);
        m = Regex.Match(name, @"^(Left|Right)(?=[A-Z])");
        if (m.Success) return (m.Groups[1].Value, name[m.Groups[1].Length..]);
        if (name.Length > 1 && (name[0] == 'L' || name[0] == 'R') && char.IsUpper(name[1]))
        {
            var t = SideToken.Match(name, 1);
            if (t.Success)
            {
                var rest = (name[..t.Index] + name[(t.Index + t.Length)..]).Trim('.', '_');
                return (t.Groups[1].Value == "L" ? "Left" : "Right", rest.Length > 0 ? rest : name);
            }
            if (SingleSide.IsMatch(name)) return (name[0] == 'L' ? "Left" : "Right", name[1..]);
        }
        m = Regex.Match(name, @"_([LR])_(\d+)$");
        if (m.Success) return (m.Groups[1].Value == "L" ? "Left" : "Right", name[..m.Index] + "_" + m.Groups[2].Value);
        m = Regex.Match(name, @"_([LR])$");
        if (m.Success) return (m.Groups[1].Value == "L" ? "Left" : "Right", name[..m.Index]);
        return ("", name);
    }

    static string SplitAndMapTokens(string name)
    {
        var mapped = new List<string>();
        foreach (var t in Regex.Split(name, @"(?=[A-Z])|_"))
        {
            if (t.Length == 0) continue;
            var baseName = Regex.Replace(t, @"\d+$", "");
            var value = Canon(baseName);
            if (value is not null) { if (value.Length > 0) mapped.Add(value); }
            else if (baseName.Length > 1) mapped.Add(baseName);
        }
        return string.Join(" ", mapped);
    }

    static string WithSide(string side, string result)
    {
        if (side.Length == 0) return result;
        var m = Regex.Match(result, @"^(Left|Right)\b\s*");
        if (m.Success) result = result[m.Length..];
        return (side + " " + result).Trim();
    }

    static string CleanStandard(string raw)
    {
        var name = Namespace.Replace(raw, "", 1);
        if (name.Length == 0) name = raw;
        name = BipPrefix.Replace(name, "", 1);
        foreach (var prefix in RemovePrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        name = name.TrimStart('_').Trim();
        if (name.Length == 0) return raw.Trim('_');

        var (side, rest) = ExtractSidePrefix(name);
        name = rest;

        var m = FingerSeg.Match(name);
        if (m.Success)
        {
            var label = FingerCodeNames[int.Parse(m.Groups[1].Value) - 1] + " Finger";
            if (m.Groups[2].Value == "Tip") label += " End";
            return WithSide(side, label);
        }
        m = FingerCode.Match(name);
        if (m.Success)
        {
            var label = FingerCodeNames[int.Parse(m.Groups[1].Value)] + " Finger";
            if (m.Groups[2].Success && m.Groups[2].Value.Length > 0) label += " End";
            return WithSide(side, label);
        }

        m = Regex.Match(name, @"^(.+?)_?(\d+)$");
        var baseName = m.Success ? m.Groups[1].Value.TrimEnd('_') : name;
        var mappedBase = Canon(baseName);
        if (mappedBase is not null)
        {
            if (mappedBase.Length == 0 || mappedBase == "Bone") return "Bone";
            return WithSide(side, mappedBase);
        }

        var mapped = new List<string>();
        foreach (var p in baseName.Split('_'))
        {
            if (p.Length == 0) continue;
            if (p is "L" or "l") { if (side.Length == 0) side = "Left"; continue; }
            if (p is "R" or "r") { if (side.Length == 0) side = "Right"; continue; }
            var subBase = Regex.Replace(p, @"\d+$", "");
            var subMapped = Canon(subBase);
            if (subMapped is not null) { if (subMapped.Length > 0) mapped.Add(subMapped); }
            else if (subBase.Length > 0)
            {
                foreach (var st in Regex.Split(subBase, @"(?=[A-Z])"))
                {
                    if (st.Length == 0) continue;
                    var stMapped = Canon(st);
                    if (stMapped is not null) { if (stMapped.Length > 0) mapped.Add(stMapped); }
                    else if (st.Length > 1) mapped.Add(st);
                }
            }
        }
        var result = string.Join(" ", mapped);
        if (result.Length == 0 || result == "Bone") return "Bone";
        var sided = WithSide(side, result).Trim();
        return sided.Length > 0 ? sided : raw;
    }

    static string PostProcess(string name)
    {
        name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.', '_', ' ');
        name = Regex.Replace(name, @"\s+\d+$", "");
        name = Regex.Replace(name, @"(\w)\d+\s+End", "$1 End");
        name = Regex.Replace(name, @"\bHand (Thumb|Index|Middle|Ring|Pinky) Finger\b", "$1 Finger");
        name = Regex.Replace(name, @"\b(Thumb|Index|Middle|Ring|Pinky) Finger Finger\b", "$1 Finger");
        name = Regex.Replace(name, @"\bFinger (Thumb|Index|Middle|Ring|Pinky) Finger\b", "$1 Finger");
        name = Regex.Replace(name, @"\b(?:Up|Upper) Leg$", "Thigh");
        name = Regex.Replace(name, @"\bLower Leg$", "Shin");
        name = Regex.Replace(name, @"\b(?:Lower|Fore) Arm$", "Forearm");
        name = Regex.Replace(name, @"\bToe Base$", "Toe");
        name = Regex.Replace(name, @"^(?!Left |Right )(.+) (Left|Right)$", "$2 $1");
        name = Regex.Replace(name, @"^Head Top End$", "Head End");
        name = name.Replace(". ", " ");
        name = Regex.Replace(name, @"^Spine ?\d* ?Tail$", "Tail");
        name = Regex.Replace(name, @"^Head ?\d* ?Jaw$", "Jaw");
        name = Regex.Replace(name, @"^Head ?\d* ?Eyelid$", "Eyelid");
        name = Regex.Replace(name, @"^Head Muzzle$", "Muzzle");
        name = Regex.Replace(name, @"^Head Jaw End$", "Jaw End");
        name = Regex.Replace(name, @"^Head Brain$", "Head");
        name = Regex.Replace(name, @"Bip \d+ ", "");
        name = Regex.Replace(name, @"^(\w+) \1$", "$1");
        if (Regex.IsMatch(name, @"^Xtra")) name = "Bone";
        name = Regex.Replace(name, @"Ponytail\d*.*", "Appendage");
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.IsLower(w[0]) && w.Length > 1 ? Capitalize(w) : w);
        }
        name = sb.ToString();
        name = Regex.Replace(name, @"^Spine (Left|Right) Wing$", "$1 Wing");
        name = Regex.Replace(name, @"\s+\d+$", "");
        return name.Length == 0 ? "Bone" : name;
    }

    // generated from UniMate data_process/joint_annotation/vocab.py and face_select_rule.py
    static readonly string[] RemovePrefixes = { "BN_Bip01_", "BN_Bip01 ", "Base Human ", "Bip001_", "Bip001 ", "Bip01_", "Bip01 ", "Preset01_", "Preset01 ", "Mutant:", "Sif:", "BN_", "NPC_", "jt_", "Bn_", "bn_", "b_" };
    static readonly Dictionary<string, string> Canonical = new(StringComparer.Ordinal)
    {
        ["Pelvis"] = "Pelvis",
        ["pelv"] = "Pelvis",
        ["Spine"] = "Spine",
        ["Spn"] = "Spine",
        ["Spline"] = "Spine",
        ["Ribcage"] = "Ribcage",
        ["Neck"] = "Neck",
        ["Nek"] = "Neck",
        ["Head"] = "Head",
        ["Scull"] = "Skull",
        ["Skull"] = "Skull",
        ["ScullBase"] = "Skull Base",
        ["SkullBase"] = "Skull Base",
        ["HeadNub"] = "Head End",
        ["Nub"] = "End",
        ["Tip"] = "End",
        ["Hips"] = "Hips",
        ["Cog"] = "Root",
        ["Clavicle"] = "Shoulder",
        ["Collarbone"] = "Shoulder",
        ["Scapula"] = "Scapula",
        ["UpperArm"] = "Upper Arm",
        ["Upperarm"] = "Upper Arm",
        ["Humerus"] = "Upper Arm",
        ["Forearm"] = "Forearm",
        ["ForeArm"] = "Forearm",
        ["LowerArm"] = "Forearm",
        ["Lowerarm"] = "Forearm",
        ["Radius"] = "Forearm",
        ["Hand"] = "Hand",
        ["Hnd"] = "Hand",
        ["Palm"] = "Palm",
        ["Wrist"] = "Wrist",
        ["Elbow"] = "Elbow",
        ["Shoulder"] = "Shoulder",
        ["Thigh"] = "Thigh",
        ["Femur"] = "Thigh",
        ["UpLeg"] = "Thigh",
        ["UpperLeg"] = "Thigh",
        ["Upperleg"] = "Thigh",
        ["Calf"] = "Shin",
        ["Tibia"] = "Shin",
        ["Leg"] = "Leg",
        ["LowerLeg"] = "Shin",
        ["Lowerleg"] = "Shin",
        ["HorseLink"] = "Fetlock",
        ["LargeCannon"] = "Cannon",
        ["Metacarpus"] = "Metacarpus",
        ["PhalangesManus"] = "Phalanges",
        ["PhalanxPrima"] = "Pastern",
        ["Foot"] = "Foot",
        ["Ankle"] = "Ankle",
        ["Heel"] = "Heel",
        ["Toe"] = "Toe",
        ["Toes"] = "Toe",
        ["ToeBase"] = "Toe",
        ["Ball"] = "Toe",
        ["Hoof"] = "Hoof",
        ["Knee"] = "Knee",
        ["Foreleg"] = "Front Leg",
        ["Hindleg"] = "Hind Leg",
        ["Finger"] = "Finger",
        ["Thumb"] = "Thumb Finger",
        ["Pinky"] = "Pinky Finger",
        ["Little"] = "Pinky Finger",
        ["Ring"] = "Ring Finger",
        ["Middle"] = "Middle Finger",
        ["Index"] = "Index Finger",
        ["Jaw"] = "Jaw",
        ["Tongue"] = "Tongue",
        ["Thouge"] = "Tongue",
        ["Tone"] = "Tongue",
        ["tunge"] = "Tongue",
        ["Tunge"] = "Tongue",
        ["Ear"] = "Ear",
        ["Eye"] = "Eye",
        ["Eyeball"] = "Eyeball",
        ["EyeBall"] = "Eyeball",
        ["Eyebrow"] = "Eyebrow",
        ["Eyelid"] = "Eyelid",
        ["Eyeleds"] = "Eyelid",
        ["Mouth"] = "Mouth",
        ["Lip"] = "Lip",
        ["Nose"] = "Nose",
        ["Muzzle"] = "Muzzle",
        ["Chin"] = "Chin",
        ["Cheek"] = "Cheek",
        ["Torso"] = "Body",
        ["LegAnkle"] = "Ankle",
        ["Fingers"] = "Finger",
        ["Tail"] = "Tail",
        ["Tai"] = "Tail",
        ["Wing"] = "Wing",
        ["RWing"] = "Right Wing",
        ["LWing"] = "Left Wing",
        ["Feather"] = "Feather",
        ["Feeler"] = "Antenna",
        ["Antenna"] = "Antenna",
        ["Feelers"] = "Barbel",
        ["Tentacle"] = "Tentacle",
        ["Tentacles"] = "Tentacle",
        ["Claw"] = "Claw",
        ["HandClaw"] = "Hand Claw",
        ["Fang"] = "Fang",
        ["Fangs"] = "Fang",
        ["Mandible"] = "Mandible",
        ["BigMandible"] = "Large Mandible",
        ["LowerMandible"] = "Lower Mandible",
        ["Pincer"] = "Pincer",
        ["pincers"] = "Pincer",
        ["Pliers"] = "Pincer",
        ["Piers"] = "Pincer",
        ["Mane"] = "Mane",
        ["Fur"] = "Fur",
        ["Hair"] = "Mane",
        ["Beard"] = "Whisker",
        ["Mascara"] = "Whisker",
        ["Shell"] = "Shell",
        ["Stinger"] = "Stinger",
        ["Horn"] = "Horn",
        ["dorsal"] = "Dorsal Plate",
        ["Fin"] = "Fin",
        ["ponitail"] = "Crest",
        ["Ponytail"] = "Appendage",
        ["Downbody"] = "Lower Body",
        ["Down"] = "Lower Body",
        ["Body"] = "Body",
        ["Reins"] = "Reins",
        ["Halter"] = "Halter",
        ["Jiggle"] = "Jiggle",
        ["TwistBone"] = "Twist",
        ["UpperArmTwist"] = "Upper Arm Twist",
        ["ForearmTwist"] = "Forearm Twist",
        ["ThighMuscle"] = "Thigh Muscle",
        ["NeckMuscle"] = "Neck Muscle",
        ["Clip"] = "Mandible",
        ["Wings"] = "Wing",
        ["Shall"] = "Mandible",
        ["Trajectory"] = "Root",
        ["locator"] = "Root",
        ["locator2"] = "Root",
        ["center"] = "Center",
        ["MagicEffectsNode"] = "Bone",
        ["Handle"] = "Handle",
        ["IK_Chain"] = "IK Chain",
        ["Joint"] = "",
        ["Jnt"] = "",
        ["Internal"] = "",
        ["Def"] = "",
        ["Part"] = "",
        ["Armature"] = "",
        ["Mixamorig"] = "",
        ["Character"] = "",
        ["Rig"] = "",
        ["Bip"] = "",
        ["Base"] = "",
        ["Human"] = "",
        ["Mid"] = "",
        ["Controller"] = "",
        ["Controler"] = "",
        ["Ctrl"] = "",
        ["Quick"] = "",
        ["Untitled"] = "",
        ["Pasted"] = "",
        ["Bind"] = "",
        ["Skeleton"] = "",
        ["Reference"] = "",
        ["Res"] = "",
        ["Main"] = "",
        ["Metacarpal"] = "",
        ["Proximal"] = "",
        ["Intermediate"] = "",
        ["Medial"] = "",
        ["Distal"] = "",
        ["Digit"] = "Finger",
        ["FrontLeg"] = "Front Leg",
        ["MiddleLeg"] = "Middle Leg",
        ["HindLeg"] = "Hind Leg",
        ["Crab_pincers"] = "Pincer",
        ["SpineR"] = "Right Abdomen",
        ["SpineL"] = "Left Abdomen",
    };
    static readonly Dictionary<string, string> MixamoMap = new(StringComparer.Ordinal)
    {
        ["Hips"] = "Hips",
        ["Spine"] = "Spine",
        ["Spine1"] = "Spine",
        ["Spine2"] = "Spine",
        ["Neck"] = "Neck",
        ["Head"] = "Head",
        ["HeadTop_End"] = "Head End",
        ["Eye"] = "Eye",
        ["Shoulder"] = "Shoulder",
        ["Arm"] = "Upper Arm",
        ["ForeArm"] = "Forearm",
        ["Hand"] = "Hand",
        ["HandThumb"] = "Thumb Finger",
        ["HandIndex"] = "Index Finger",
        ["HandMiddle"] = "Middle Finger",
        ["HandRing"] = "Ring Finger",
        ["HandPinky"] = "Pinky Finger",
        ["UpLeg"] = "Thigh",
        ["Leg"] = "Shin",
        ["Foot"] = "Foot",
        ["ToeBase"] = "Toe",
        ["Toe_End"] = "Toe End",
    };
    public static readonly string[] SymmetricPairPriority = { "Thigh", "Shoulder", "Front Shoulder", "Back Hip", "Hip", "Scapula", "Upper Arm", "Arm", "Front Leg", "Hind Leg", "Middle Leg", "Back Leg", "Wing", "Leg", "Pectoral Fin", "Pelvic Fin", "Fin", "Gill", "Pincer", "Mandible", "Large Mandible", "Lower Mandible", "Stinger", "Claw", "Hand Claw", "Antenna", "Forearm", "Shin", "Knee", "Elbow", "Ankle", "Wrist", "Hand", "Palm", "Foot", "Heel", "Front Paw", "Back Paw", "Paw", "Front Hoof", "Rear Hoof", "Hoof", "Fetlock", "Cannon", "Metacarpus", "Pastern", "Toe", "Thumb Finger", "Index Finger", "Middle Finger", "Ring Finger", "Pinky Finger", "Finger", "Neck", "Eye", "Eyeball", "Eyelid", "Eyebrow", "Ear", "Horn", "Cheek", "Whisker", "Fang", "Barbel", "Tentacle", "Feather", "Tail" };
    public static readonly string[] TailKeywords = { "Tail", "Tail Twist" };
    public static readonly string[] HeadKeywords = { "Chin", "Head", "Head End", "Jaw", "Lower Jaw", "Muzzle", "Nose", "Skull", "Skull Base", "Tongue", "Upper Jaw" };
}

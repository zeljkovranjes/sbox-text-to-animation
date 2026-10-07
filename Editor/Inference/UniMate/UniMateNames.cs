using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>
/// Port of upstream UniMate's rule-based joint annotation: <c>data_process/joint_annotation/vocab.py</c>,
/// <c>names_clean_rule.py</c> (raw bone name -> clean anatomical label the model's name embedding was trained on)
/// and <c>face_select_rule.py</c> (the symmetric joint pair, or body-axis ends, that define the facing). Kept
/// line-for-line with the Python so fixture comparisons stay exact; Python's <c>str.capitalize</c> and zero-width
/// <c>re.split</c> are reproduced explicitly.
/// </summary>
public static class UniMateNames
{
	// =====================================================================================================
	// vocab.py
	// =====================================================================================================

	static readonly string[] RemovePrefixes =
	{
		"BN_Bip01_", "BN_Bip01 ", "Base Human ", "Bip001_", "Bip001 ", "Bip01_", "Bip01 ", "Preset01_", "Preset01 ",
		"Mutant:", "Sif:", "BN_", "NPC_", "jt_", "Bn_", "bn_", "b_",
	};

	static readonly Dictionary<string, string> MixamoMap = new()
	{
		["Hips"] = "Hips", ["Spine"] = "Spine", ["Spine1"] = "Spine", ["Spine2"] = "Spine", ["Neck"] = "Neck",
		["Head"] = "Head", ["HeadTop_End"] = "Head End", ["Eye"] = "Eye", ["Shoulder"] = "Shoulder",
		["Arm"] = "Upper Arm", ["ForeArm"] = "Forearm", ["Hand"] = "Hand",
		["HandThumb"] = "Thumb Finger", ["HandIndex"] = "Index Finger", ["HandMiddle"] = "Middle Finger",
		["HandRing"] = "Ring Finger", ["HandPinky"] = "Pinky Finger",
		["UpLeg"] = "Thigh", ["Leg"] = "Shin", ["Foot"] = "Foot", ["ToeBase"] = "Toe", ["Toe_End"] = "Toe End",
	};

	static readonly Dictionary<string, string> JapaneseWords = new()
	{
		["momo"] = "Thigh", ["sippo"] = "Tail", ["mune"] = "Chest", ["hiza"] = "Knee", ["hara"] = "Abdomen",
		["ashi"] = "Foot", ["hiji"] = "Elbow", ["koshi"] = "Hips", ["kosi"] = "Hips", ["te"] = "Hand",
		["kubi"] = "Neck", ["atama"] = "Head", ["ago"] = "Jaw", ["kata"] = "Shoulder", ["kao"] = "Head", ["o"] = "Tail",
		["munabire"] = "Pectoral Fin", ["era"] = "Gill", ["obire"] = "Caudal Fin", ["sebire"] = "Dorsal Fin",
		["harabire"] = "Pelvic Fin", ["shiribire"] = "Anal Fin", ["shippo"] = "Tail",
	};

	static readonly Dictionary<string, string> JapaneseWordsLower = JapaneseWords.ToDictionary( kv => kv.Key.ToLowerInvariant(), kv => kv.Value );

	static readonly Dictionary<string, string> Canonical = new()
	{
		["Pelvis"] = "Pelvis", ["pelv"] = "Pelvis", ["Spine"] = "Spine", ["Spn"] = "Spine", ["Spline"] = "Spine",
		["Ribcage"] = "Ribcage", ["Neck"] = "Neck", ["Nek"] = "Neck", ["Head"] = "Head", ["Scull"] = "Skull", ["Skull"] = "Skull",
		["ScullBase"] = "Skull Base", ["SkullBase"] = "Skull Base", ["HeadNub"] = "Head End", ["Nub"] = "End", ["Tip"] = "End",
		["Hips"] = "Hips", ["Cog"] = "Root",
		["Clavicle"] = "Shoulder", ["Collarbone"] = "Shoulder", ["Scapula"] = "Scapula",
		["UpperArm"] = "Upper Arm", ["Upperarm"] = "Upper Arm", ["Humerus"] = "Upper Arm",
		["Forearm"] = "Forearm", ["ForeArm"] = "Forearm", ["LowerArm"] = "Forearm", ["Lowerarm"] = "Forearm", ["Radius"] = "Forearm",
		["Hand"] = "Hand", ["Hnd"] = "Hand", ["Palm"] = "Palm", ["Wrist"] = "Wrist", ["Elbow"] = "Elbow", ["Shoulder"] = "Shoulder",
		["Thigh"] = "Thigh", ["Femur"] = "Thigh", ["UpLeg"] = "Thigh", ["UpperLeg"] = "Thigh", ["Upperleg"] = "Thigh",
		["Calf"] = "Shin", ["Tibia"] = "Shin", ["Leg"] = "Leg", ["LowerLeg"] = "Shin", ["Lowerleg"] = "Shin",
		["HorseLink"] = "Fetlock", ["LargeCannon"] = "Cannon", ["Metacarpus"] = "Metacarpus", ["PhalangesManus"] = "Phalanges",
		["PhalanxPrima"] = "Pastern", ["Foot"] = "Foot", ["Ankle"] = "Ankle", ["Heel"] = "Heel",
		["Toe"] = "Toe", ["Toes"] = "Toe", ["ToeBase"] = "Toe", ["Ball"] = "Toe", ["Hoof"] = "Hoof", ["Knee"] = "Knee",
		["Foreleg"] = "Front Leg", ["Hindleg"] = "Hind Leg",
		["Finger"] = "Finger", ["Thumb"] = "Thumb Finger", ["Pinky"] = "Pinky Finger", ["Little"] = "Pinky Finger",
		["Ring"] = "Ring Finger", ["Middle"] = "Middle Finger", ["Index"] = "Index Finger",
		["Jaw"] = "Jaw", ["Tongue"] = "Tongue", ["Thouge"] = "Tongue", ["Tone"] = "Tongue", ["tunge"] = "Tongue", ["Tunge"] = "Tongue",
		["Ear"] = "Ear", ["Eye"] = "Eye", ["Eyeball"] = "Eyeball", ["EyeBall"] = "Eyeball", ["Eyebrow"] = "Eyebrow",
		["Eyelid"] = "Eyelid", ["Eyeleds"] = "Eyelid", ["Mouth"] = "Mouth", ["Lip"] = "Lip", ["Nose"] = "Nose", ["Muzzle"] = "Muzzle",
		["Chin"] = "Chin", ["Cheek"] = "Cheek", ["Torso"] = "Body", ["LegAnkle"] = "Ankle", ["Fingers"] = "Finger",
		["Tail"] = "Tail", ["Tai"] = "Tail", ["Wing"] = "Wing", ["RWing"] = "Right Wing", ["LWing"] = "Left Wing", ["Feather"] = "Feather",
		["Feeler"] = "Antenna", ["Antenna"] = "Antenna", ["Feelers"] = "Barbel", ["Tentacle"] = "Tentacle", ["Tentacles"] = "Tentacle",
		["Claw"] = "Claw", ["HandClaw"] = "Hand Claw", ["Fang"] = "Fang", ["Fangs"] = "Fang",
		["Mandible"] = "Mandible", ["BigMandible"] = "Large Mandible", ["LowerMandible"] = "Lower Mandible",
		["Pincer"] = "Pincer", ["pincers"] = "Pincer", ["Pliers"] = "Pincer", ["Piers"] = "Pincer",
		["Mane"] = "Mane", ["Fur"] = "Fur", ["Hair"] = "Mane", ["Beard"] = "Whisker", ["Mascara"] = "Whisker",
		["Shell"] = "Shell", ["Stinger"] = "Stinger", ["Horn"] = "Horn", ["dorsal"] = "Dorsal Plate", ["Fin"] = "Fin",
		["ponitail"] = "Crest", ["Ponytail"] = "Appendage", ["Downbody"] = "Lower Body", ["Down"] = "Lower Body", ["Body"] = "Body",
		["Reins"] = "Reins", ["Halter"] = "Halter",
		["Jiggle"] = "Jiggle", ["TwistBone"] = "Twist", ["UpperArmTwist"] = "Upper Arm Twist", ["ForearmTwist"] = "Forearm Twist",
		["ThighMuscle"] = "Thigh Muscle", ["NeckMuscle"] = "Neck Muscle",
		["Clip"] = "Mandible", ["Wings"] = "Wing", ["Shall"] = "Mandible",
		["Trajectory"] = "Root", ["locator"] = "Root", ["locator2"] = "Root", ["center"] = "Center",
		["MagicEffectsNode"] = "Bone", ["Handle"] = "Handle", ["IK_Chain"] = "IK Chain",
		["Joint"] = "", ["Jnt"] = "", ["Internal"] = "", ["Def"] = "", ["Part"] = "", ["Armature"] = "",
		["Mixamorig"] = "", ["Character"] = "", ["Rig"] = "", ["Bip"] = "", ["Base"] = "", ["Human"] = "", ["Mid"] = "",
		["Controller"] = "", ["Controler"] = "", ["Ctrl"] = "", ["Quick"] = "", ["Untitled"] = "", ["Pasted"] = "",
		["Bind"] = "", ["Skeleton"] = "", ["Reference"] = "", ["Res"] = "", ["Main"] = "",
		["Metacarpal"] = "", ["Proximal"] = "", ["Intermediate"] = "", ["Medial"] = "", ["Distal"] = "",
		["Digit"] = "Finger", ["FrontLeg"] = "Front Leg", ["MiddleLeg"] = "Middle Leg", ["HindLeg"] = "Hind Leg",
		["Crab_pincers"] = "Pincer", ["SpineR"] = "Right Abdomen", ["SpineL"] = "Left Abdomen",
	};

	static readonly Dictionary<string, string> StandaloneMap = new()
	{
		["locator"] = "Root", ["locator2"] = "Root", ["EyesBlue_2"] = "Eye", ["MagicEffectsNode"] = "Bone",
		["Handle"] = "Handle", ["IK_Chain01"] = "IK Chain", ["BN_P"] = "Belly",
		["Hips"] = "Hips", ["Spine"] = "Spine", ["Head"] = "Head", ["Trajectory"] = "Root",
		["RightArm"] = "Right Upper Arm", ["RightForeArm"] = "Right Forearm", ["LeftArm"] = "Left Upper Arm", ["LeftForeArm"] = "Left Forearm",
		["RightLeg"] = "Right Shin", ["LeftLeg"] = "Left Shin", ["RightUpLeg"] = "Right Thigh", ["LeftUpLeg"] = "Left Thigh",
		["RightFoot"] = "Right Foot", ["LeftFoot"] = "Left Foot", ["RightHand"] = "Right Hand", ["LeftHand"] = "Left Hand",
		["Tail01"] = "Tail", ["Tail02"] = "Tail",
	};

	static readonly Dictionary<string, string> SabrecatMap = new()
	{
		["Sabrecat__pelv_"] = "Pelvis", ["Sabrecat_LeftThigh_LThi_"] = "Left Thigh", ["Sabrecat_LeftCalf_LClf_"] = "Left Shin",
		["Sabrecat_LeftFoot_LFot_"] = "Left Foot", ["Sabrecat_LeftToe0_LT00_"] = "Left Toe", ["Sabrecat_LeftToe0_LT01_"] = "Left Toe",
		["Sabrecat_RightThigh_RThi_"] = "Right Thigh", ["Sabrecat_RightCalf_RClf_"] = "Right Shin", ["Sabrecat_RightFoot_RFot_"] = "Right Foot",
		["Sabrecat_RightToe0_RT00_"] = "Right Toe", ["Sabrecat_RightToe0_RT01_"] = "Right Toe",
		["Sabrecat_Tail0_Tal0_"] = "Tail", ["Sabrecat_Tail1_Tal1_"] = "Tail", ["Sabrecat_Tail2_Tal2_"] = "Tail",
		["Sabrecat_Spine_Spn0_"] = "Spine", ["Sabrecat_Spine_Spn1_"] = "Spine", ["MagicEffectsNode"] = "Bone",
		["Sabrecat_Spine_Spn2_"] = "Spine", ["Sabrecat_Spine_Spn3_"] = "Spine", ["Sabrecat_Ribcage_Spn4_"] = "Ribcage",
		["Sabrecat_Ribcage_Spn1_"] = "Ribcage", ["Sabrecat_Neck_Nek0_"] = "Neck", ["Sabrecat_Neck_Nek1_"] = "Neck",
		["Sabrecat_Neck_Nek2_"] = "Neck", ["Sabrecat_Head__Head_"] = "Head", ["Sabrecat_Head__LEye_"] = "Left Eye",
		["Sabrecat_Head__RChk_"] = "Right Cheek", ["Sabrecat_Head_Head__LChk_"] = "Left Cheek", ["Sabrecat_HeadLeftEar_LEar_"] = "Left Ear",
		["Sabrecat_Head_EyeLid_HELT_"] = "Upper Eyelid", ["Sabrecat_Head__REye_"] = "Right Eye", ["Sabrecat_Head_jaw_"] = "Jaw",
		["Sabrecat_HeadEyeLid__HELB_"] = "Lower Eyelid", ["Sabrecat_HeadRightEar_REar_"] = "Right Ear",
		["Sabrecat_Head_LM01_"] = "Left Mouth", ["Sabrecat_Head_RM01_"] = "Right Mouth",
		["Sabrecat_RightClavicle_RClv_"] = "Right Shoulder", ["Sabrecat_RightUpperArm_RUar_"] = "Right Upper Arm",
		["Sabrecat_RightForearm_RFar_"] = "Right Forearm", ["Sabrecat_RightTwistBone_RFTB_"] = "Right Twist",
		["Sabrecat_RightHand_RHnd_"] = "Right Hand", ["Sabrecat_RightFinger3_RF30_"] = "Right Finger", ["Sabrecat_RightFinger3_RF31_"] = "Right Finger",
		["Sabrecat_RightFinger2_RF20_"] = "Right Finger", ["Sabrecat_RightFinger2_RF21_"] = "Right Finger", ["Sabrecat_Finger4_RF04_"] = "Right Finger",
		["Sabrecat_RightFinger1_RF10_"] = "Right Finger", ["Sabrecat_RightFinger1_RF11_"] = "Right Finger",
		["Sabrecat_RightFinger0_RF00_"] = "Right Finger", ["Sabrecat_RightFinger0_RF01_"] = "Right Finger",
		["Sabrecat_LeftClavicle_LClv_"] = "Left Shoulder", ["Sabrecat_LeftUpperArm_LUar_"] = "Left Upper Arm",
		["Sabrecat_LeftForearm_LFar_"] = "Left Forearm", ["Sabrecat_LeftTwistBone_LFTB_"] = "Left Twist", ["Sabrecat_LeftHand_LHnd_"] = "Left Hand",
		["Sabrecat_Finger4_LF04_"] = "Left Finger", ["Sabrecat_LeftFinger1_LF10_"] = "Left Finger", ["Sabrecat_LeftFinger1_LF11_"] = "Left Finger",
		["Sabrecat_LeftFinger2_LF20_"] = "Left Finger", ["Sabrecat_LeftFinger2_LF21_"] = "Left Finger", ["Sabrecat_LeftFinger3_RF30_"] = "Left Finger",
		["Sabrecat_LeftFinger3_RF31_"] = "Left Finger", ["Sabrecat_LeftFinger0_LF00_"] = "Left Finger", ["Sabrecat_LeftFinger0_LF01_"] = "Left Finger",
	};

	static readonly Dictionary<string, string> PirranaCompounds = new()
	{
		["munabireR"] = "Right Pectoral Fin", ["munabireL"] = "Left Pectoral Fin", ["eraR"] = "Right Gill", ["eraL"] = "Left Gill",
		["shippoA"] = "Tail", ["shippoB"] = "Tail", ["shiribire"] = "Anal Fin", ["shirihireB"] = "Anal Fin", ["shiribireA"] = "Anal Fin",
		["obire"] = "Caudal Fin", ["obireB"] = "Caudal Fin", ["obireA"] = "Caudal Fin", ["sebire"] = "Dorsal Fin",
		["harabireR"] = "Right Pelvic Fin", ["harabireL"] = "Left Pelvic Fin",
	};

	static readonly HashSet<string> JapaneseCompoundNames = new()
	{
		"munabireR", "munabireL", "eraR", "eraL", "shippoA", "shippoB", "shiribire", "shirihireB", "shiribireA", "obire", "obireB",
		"obireA", "sebire", "harabireR", "harabireL", "locator", "locator2", "kosi", "kao",
	};

	static readonly HashSet<string> JapaneseCompoundAnimals = new() { "Pirrana", "Tukan", "Alligator" };

	static readonly Dictionary<string, string> NpcDirect = new()
	{
		["Pelvis"] = "Pelvis", ["Ribcage"] = "Ribcage", ["Spine1"] = "Spine", ["Spine2"] = "Spine", ["Spine3"] = "Spine", ["Spine4"] = "Spine",
		["Neck1"] = "Neck", ["Neck2"] = "Neck", ["Head"] = "Head", ["Jaw"] = "Jaw", ["Nose"] = "Nose",
		["UpperRightLip"] = "Upper Right Lip", ["UpperLeftLip"] = "Upper Left Lip", ["UpperLip"] = "Upper Lip",
		["LowerLeftLip"] = "Lower Left Lip", ["LowerFrontLip"] = "Lower Front Lip", ["LowerRightLip"] = "Lower Right Lip",
		["Eyebrow"] = "Eyebrow", ["Leg1"] = "Thigh", ["Leg2"] = "Shin", ["LegAnkle"] = "Ankle", ["LegBall1"] = "Foot", ["Toe"] = "Toe",
		["Arm1"] = "Upper Arm", ["Arm2"] = "Forearm", ["ArmCollarbone"] = "Shoulder", ["ArmPalm"] = "Hand", ["ArmBall1"] = "Foot",
		["Arm1_UpperArmTwist1"] = "Upper Arm Twist", ["Arm1_UpperArmTwist2"] = "Upper Arm Twist",
		["Arm2_ForearmTwist1"] = "Forearm Twist", ["Arm2_ForearmTwist2"] = "Forearm Twist",
		["Pinky01"] = "Pinky Finger", ["Pinky02"] = "Pinky Finger", ["Ring01"] = "Ring Finger", ["Ring02"] = "Ring Finger",
		["Middle01"] = "Middle Finger", ["Middle02"] = "Middle Finger", ["Thumb01"] = "Thumb Finger", ["Thumb02"] = "Thumb Finger",
		["Index01"] = "Index Finger", ["Index02"] = "Index Finger",
	};

	static readonly Dictionary<string, string> ElkMap = new()
	{
		["Femur"] = "Thigh", ["Tibia"] = "Shin", ["LargeCannon"] = "Cannon", ["PhalanxPrima"] = "Pastern",
		["RearHoof"] = "Rear Hoof", ["FrontHoof"] = "Front Hoof", ["Scapula"] = "Scapula", ["Humerus"] = "Upper Arm",
		["Radius"] = "Forearm", ["Metacarpus"] = "Metacarpus", ["PhalangesManus"] = "Phalanges",
		["Spine1"] = "Spine", ["Spine2"] = "Spine", ["Spine3"] = "Spine", ["Ribcage"] = "Ribcage",
		["Neck1"] = "Neck", ["Neck2"] = "Neck", ["Neck3"] = "Neck", ["Neck4"] = "Neck", ["ScullBase"] = "Skull Base", ["Scull"] = "Skull",
		["Ear"] = "Ear", ["REar"] = "Right Ear", ["LEar"] = "Left Ear", ["Jaw"] = "Jaw", ["UpperLip"] = "Upper Lip",
		["Pelvis"] = "Pelvis", ["Tail1"] = "Tail", ["Tail2"] = "Tail",
	};

	static readonly Dictionary<string, string> JtMap = new()
	{
		["Cog"] = "Root", ["Spine1"] = "Spine", ["Spine2"] = "Spine", ["Hips"] = "Hips", ["Hip"] = "Hip",
		["Thigh"] = "Thigh", ["Knee"] = "Knee", ["Ankle"] = "Ankle", ["Foot"] = "Foot",
		["ToeMiddle"] = "Middle Toe", ["ToeInner"] = "Inner Toe", ["ToeOutter"] = "Outer Toe",
		["ClawMiddle"] = "Middle Claw", ["ClawInner"] = "Inner Claw", ["ClawOutter"] = "Outer Claw", ["ClawBack"] = "Back Claw",
		["ClawMiddle2"] = "Middle Claw", ["ClawInner2"] = "Inner Claw", ["ClawOutter2"] = "Outer Claw", ["ClawBack2"] = "Back Claw",
		["Neck1"] = "Neck", ["Neck2"] = "Neck", ["Neck3"] = "Neck", ["Head"] = "Head", ["Jaw"] = "Jaw",
		["Tongue1"] = "Tongue", ["Tongue2"] = "Tongue", ["Eye"] = "Eye", ["EyeBall"] = "Eyeball",
		["Shoulder"] = "Shoulder", ["Elbow"] = "Elbow", ["Wrist"] = "Wrist", ["WristBack"] = "Wrist Back", ["ElbowBack"] = "Elbow Back",
		["Clavicle"] = "Shoulder", ["FingerMiddle"] = "Middle Finger", ["FingerInner"] = "Inner Finger", ["FingerOutter"] = "Outer Finger",
		["HandClawMiddle"] = "Middle Hand Claw", ["HandClawInner"] = "Inner Hand Claw", ["HandClawOutter"] = "Outer Hand Claw",
		["Tail1"] = "Tail", ["Tail2"] = "Tail", ["Tail3"] = "Tail", ["Tail4"] = "Tail", ["Tail5"] = "Tail", ["Tail6"] = "Tail",
		["ThighMuscle"] = "Thigh Muscle", ["NeckMuscle"] = "Neck Muscle",
		["Tail01"] = "Tail", ["Tail02"] = "Tail", ["Tail03"] = "Tail", ["Tail04"] = "Tail", ["Tail05"] = "Tail", ["Tail06"] = "Tail",
		["Tail07"] = "Tail", ["Tail08"] = "Tail", ["Tail09"] = "Tail",
		["Tail01x"] = "Tail Twist", ["Tail02x"] = "Tail Twist", ["Tail03x"] = "Tail Twist", ["Tail04x"] = "Tail Twist",
		["Tail05x"] = "Tail Twist", ["Tail06x"] = "Tail Twist", ["Tail07x"] = "Tail Twist", ["Tail08x"] = "Tail Twist",
		["FrontLeg1"] = "Front Leg", ["FrontLeg2"] = "Front Leg", ["FrontLeg3"] = "Front Leg", ["FrontLeg4End"] = "Front Leg End",
		["MiddleLeg1"] = "Middle Leg", ["MiddleLeg2"] = "Middle Leg", ["MiddleLeg3"] = "Middle Leg", ["MiddleLeg4End"] = "Middle Leg End",
		["HindLeg1"] = "Hind Leg", ["HindLeg2"] = "Hind Leg", ["HindLeg3"] = "Hind Leg", ["HindLeg4End"] = "Hind Leg End",
		["BigMandible"] = "Large Mandible", ["BigMandibleMid"] = "Large Mandible",
		["LowerMandible"] = "Lower Mandible", ["LowerMandibleMid"] = "Lower Mandible", ["Fangs"] = "Fang", ["FangsMid"] = "Fang",
	};

	static readonly Dictionary<string, string> SpiderMap = new()
	{
		["_body_"] = "Body", ["NPC_L_MagicNode__LMag_"] = "Bone", ["ArmRCollarbone"] = "Right Shoulder",
		["ArmR_01_"] = "Right Arm", ["ArmR_02_"] = "Right Arm", ["ArmRClaw"] = "Right Claw", ["ArmLCollarbone"] = "Left Shoulder",
		["ArmL_01_"] = "Left Arm", ["ArmL_02_"] = "Left Arm", ["ArmLClaw"] = "Left Claw",
		["Tail1"] = "Tail", ["Tail2"] = "Tail", ["Tail3"] = "Tail", ["R_Jaw_"] = "Right Jaw", ["L_Jaw_"] = "Left Jaw",
	};

	// =====================================================================================================
	// names_clean_rule.py
	// =====================================================================================================

	static readonly Regex SideTokenRe = new( @"[._]([LR])(?=[._]|\d|$)" );
	static readonly Regex SingleSideRe = new( @"^[LR](?:[A-Z][a-z]|[A-Z]{0,2}_[A-Za-z])" );
	static readonly Regex TrailingTokenRe = new( @"[._](\d+|[LRlr]|x)$" );
	static readonly Regex FingerCodeRe = new( @"^[Ff]inger([0-4])\d*(Nub)?$" );
	static readonly Dictionary<string, string> FingerCode = new() { ["0"] = "Thumb", ["1"] = "Index", ["2"] = "Middle", ["3"] = "Ring", ["4"] = "Pinky" };
	static readonly Regex FingerSegRe = new( @"^[Ff]inger([1-5])(Metacarpal|Proximal|Medial|Distal|Tip)\d*$" );
	static readonly Dictionary<string, string> FingerOrd = new() { ["1"] = "Thumb", ["2"] = "Index", ["3"] = "Middle", ["4"] = "Ring", ["5"] = "Pinky" };
	static readonly Regex ParenDecorRe = new( @"\s*\([^)]*\)" );
	static readonly Regex NamespaceRe = new( @"^[A-Za-z][\w .-]*:" );
	static readonly Regex BipPrefixRe = new( @"^(?:BN_)?Bip\d+(?:[-_ ]+|(?=[LR][A-Z]))" );
	static readonly Regex MixamorigRe = new( @"^mixamorig\d*[:_]", RegexOptions.IgnoreCase );

	/// <summary>Python's <c>str.capitalize()</c>: first character upper, the rest lower.</summary>
	static string Capitalize( string s ) => s.Length == 0 ? s : char.ToUpperInvariant( s[0] ) + s[1..].ToLowerInvariant();

	/// <summary>Python's <c>re.split(pattern, s)</c>, including empty pieces at zero-width matches.</summary>
	static List<string> PySplit( string s, Regex pattern )
	{
		var parts = new List<string>();
		var last = 0;
		foreach ( Match m in pattern.Matches( s ) )
		{
			// Python skips an empty match adjacent to the previous match's end only when that match was empty too
			parts.Add( s[last..m.Index] );
			last = m.Index + m.Length;
		}
		parts.Add( s[last..] );
		return parts;
	}

	static readonly Regex CamelOrUnderscore = new( @"(?=[A-Z])|_" );
	static readonly Regex CamelSplit = new( @"(?=[A-Z])" );

	static (string Side, string Base) StripTrailingDecorations( string name )
	{
		var side = "";
		while ( true )
		{
			var m = TrailingTokenRe.Match( name );
			if ( !m.Success ) return (side, name);
			var tok = m.Groups[1].Value;
			if ( tok is "L" or "l" ) side = "Left";
			else if ( tok is "R" or "r" ) side = "Right";
			name = name[..m.Index];
		}
	}

	static string Canon( string token )
	{
		if ( token is null ) return null;
		if ( Canonical.TryGetValue( token, out var value ) ) return value;
		if ( token.Length > 0 && token != Capitalize( token ) && Canonical.TryGetValue( Capitalize( token ), out value ) ) return value;
		return null;
	}

	public static (string Side, string Remaining) ExtractSidePrefix( string name )
	{
		var (side, stripped0) = StripTrailingDecorations( name );
		name = stripped0;
		if ( side.Length > 0 )
		{
			var stripped = Regex.Replace( name, @"^(?:Left|Right)(?=[A-Z])|^(?:Left|Right)[_ ]|^[LR][-_]|^[lr]_", "", RegexOptions.None, TimeSpan.FromSeconds( 1 ) );
			// re.sub with count=0 replaces every match; the alternatives are all anchored, so at most one applies
			return (side, stripped.Length > 0 ? stripped : name);
		}

		if ( Regex.IsMatch( name, @"^[RL][-_.]" ) )
			return (name[0] == 'L' ? "Left" : "Right", name[2..]);

		var m = Regex.Match( name, @"^([RL]) +" );
		if ( m.Success ) return (m.Groups[1].Value == "L" ? "Left" : "Right", name[m.Length..]);

		m = Regex.Match( name, @"^(Left|Right)_", RegexOptions.IgnoreCase );
		if ( m.Success ) return (Capitalize( m.Groups[1].Value ), name[m.Length..]);

		m = Regex.Match( name, @"^(Left|Right)(?=[A-Z])" );
		if ( m.Success ) return (m.Groups[1].Value, name[m.Groups[1].Length..]);

		if ( name.Length > 1 && (name[0] == 'L' || name[0] == 'R') && char.IsUpper( name[1] ) )
		{
			var t = SideTokenRe.Match( name, 1 );
			if ( t.Success )
			{
				var s = t.Groups[1].Value == "L" ? "Left" : "Right";
				var rest = (name[..t.Index] + name[(t.Index + t.Length)..]).Trim( '.', '_' );
				return (s, rest.Length > 0 ? rest : name);
			}
			if ( SingleSideRe.IsMatch( name ) )
				return (name[0] == 'L' ? "Left" : "Right", name[1..]);
		}

		m = Regex.Match( name, @"_([LR])_(\d+)$" );
		if ( m.Success ) return (m.Groups[1].Value == "L" ? "Left" : "Right", name[..m.Index] + "_" + m.Groups[2].Value);

		m = Regex.Match( name, @"_([LR])$" );
		if ( m.Success ) return (m.Groups[1].Value == "L" ? "Left" : "Right", name[..m.Index]);

		return ("", name);
	}

	static string SplitAndMapTokens( string name )
	{
		var mapped = new List<string>();
		foreach ( var t in PySplit( name, CamelOrUnderscore ) )
		{
			if ( t.Length == 0 ) continue;
			var b = Regex.Replace( t, @"\d+$", "" );
			var value = Canon( b );
			if ( value is not null ) { if ( value.Length > 0 ) mapped.Add( value ); }
			else if ( b.Length > 1 ) mapped.Add( b );
		}
		return string.Join( " ", mapped );
	}

	static string WithSide( string side, string result )
	{
		if ( side.Length == 0 ) return result;
		var m = Regex.Match( result, @"^(Left|Right)\b\s*" );
		if ( m.Success ) result = result[m.Length..];
		return PyStrip( side + " " + result );
	}

	/// <summary>Python's <c>str.strip()</c> (whitespace).</summary>
	static string PyStrip( string s ) => s.Trim( ' ', '\t', '\n', '\r', '\f', '\v' );

	static string CleanJapaneseName( string name )
	{
		var (side, n) = ExtractSidePrefix( name );
		var b = Regex.Replace( n, @"\d+$", "" );
		if ( JapaneseWordsLower.TryGetValue( b.ToLowerInvariant(), out var word ) ) return WithSide( side, word );
		if ( PirranaCompounds.TryGetValue( n, out var compound ) ) return compound;
		return WithSide( side, n );
	}

	static string CleanPrefixedName( string raw, string prefix, Dictionary<string, string> rigMap, bool stripTrailingC = false, string[] skipSubstrs = null )
	{
		var name = raw[prefix.Length..];
		if ( stripTrailingC && name.EndsWith( "_C", StringComparison.Ordinal ) ) name = name[..^2];
		var (side, n) = ExtractSidePrefix( name );
		foreach ( var sub in skipSubstrs ?? Array.Empty<string>() ) if ( n.Contains( sub, StringComparison.Ordinal ) ) return "";
		if ( !rigMap.TryGetValue( n, out var result ) && !rigMap.TryGetValue( Regex.Replace( n, @"\d+$", "" ), out result ) )
		{
			result = SplitAndMapTokens( n );
			if ( result.Length == 0 ) result = n;
		}
		return PyStrip( WithSide( side, result ) );
	}

	static string CleanSpiderName( string raw )
	{
		if ( SpiderMap.TryGetValue( raw, out var v ) ) return v;
		var m = Regex.Match( raw, @"^Fang([RL])_(\d+)_" );
		if ( m.Success ) return (m.Groups[1].Value == "R" ? "Right" : "Left") + " Fang";
		m = Regex.Match( raw, @"^Leg_([RL])_(\d)(\d)_" );
		if ( m.Success ) return (m.Groups[1].Value == "R" ? "Right" : "Left") + " Leg";
		m = Regex.Match( raw, @"^_([RL])Toe(\d)_" );
		if ( m.Success ) return (m.Groups[1].Value == "R" ? "Right" : "Left") + " Leg Tip";
		return raw.Trim( '_' );
	}

	static string CleanStandardName( string raw )
	{
		var name = NamespaceRe.Replace( raw, "", 1 );
		if ( name.Length == 0 ) name = raw;
		name = BipPrefixRe.Replace( name, "", 1 );
		foreach ( var prefix in RemovePrefixes )
		{
			if ( name.StartsWith( prefix, StringComparison.Ordinal ) ) { name = name[prefix.Length..]; break; }
		}
		name = PyStrip( name.TrimStart( '_' ) );
		if ( name.Length == 0 ) return raw.Trim( '_' );

		var (side, n) = ExtractSidePrefix( name );
		name = n;

		var m = FingerSegRe.Match( name );
		if ( m.Success )
		{
			var label = FingerOrd[m.Groups[1].Value] + " Finger";
			if ( m.Groups[2].Value == "Tip" ) label += " End";
			return WithSide( side, label );
		}
		m = FingerCodeRe.Match( name );
		if ( m.Success )
		{
			var label = FingerCode[m.Groups[1].Value] + " Finger";
			if ( m.Groups[2].Success && m.Groups[2].Value.Length > 0 ) label += " End";
			return WithSide( side, label );
		}

		m = Regex.Match( name, @"^(.+?)_?(\d+)$" );
		var b = m.Success ? m.Groups[1].Value.TrimEnd( '_' ) : name;

		var mappedBase = Canon( b );
		if ( mappedBase is not null )
		{
			if ( mappedBase.Length == 0 || mappedBase == "Bone" ) return "Bone";
			return WithSide( side, mappedBase );
		}

		var mapped = new List<string>();
		foreach ( var p in b.Split( '_' ).Where( p => p.Length > 0 ) )
		{
			if ( p is "L" or "l" ) { if ( side.Length == 0 ) side = "Left"; continue; }
			if ( p is "R" or "r" ) { if ( side.Length == 0 ) side = "Right"; continue; }
			var subBase = Regex.Replace( p, @"\d+$", "" );
			var subMapped = Canon( subBase );
			if ( subMapped is not null ) { if ( subMapped.Length > 0 ) mapped.Add( subMapped ); }
			else if ( subBase.Length > 0 )
			{
				foreach ( var st in PySplit( subBase, CamelSplit ).Where( t => t.Length > 0 ) )
				{
					var stMapped = Canon( st );
					if ( stMapped is not null ) { if ( stMapped.Length > 0 ) mapped.Add( stMapped ); }
					else if ( st.Length > 1 ) mapped.Add( st );
				}
			}
		}
		var result = string.Join( " ", mapped );
		if ( result.Length == 0 || result == "Bone" ) return "Bone";
		var withSide = PyStrip( WithSide( side, result ) );
		return withSide.Length > 0 ? withSide : raw;
	}

	/// <summary>Upstream <c>clean_joint_name(raw, animal)</c>.</summary>
	public static string CleanJointName( string raw, string animal )
	{
		if ( string.IsNullOrEmpty( raw ) || PyStrip( raw ).Length == 0 ) return raw;
		var r = PyStrip( ParenDecorRe.Replace( raw, "" ) );
		raw = r.Length > 0 ? r : raw;
		if ( Regex.IsMatch( raw, @"^Bone\d+$" ) ) return "Bone";
		if ( Regex.IsMatch( raw.Trim( '_' ), @"^_?\d+$" ) ) return raw;
		var mm = MixamorigRe.Match( raw );
		if ( mm.Success ) return CleanPrefixedName( raw, mm.Value, MixamoMap );
		if ( StandaloneMap.TryGetValue( raw, out var s ) ) return s;
		if ( SabrecatMap.TryGetValue( raw, out s ) ) return s;
		if ( animal == "Spider" ) return CleanSpiderName( raw );
		if ( raw.StartsWith( "Sabrecat", StringComparison.Ordinal ) ) return SabrecatMap.TryGetValue( raw, out s ) ? s : raw;
		if ( raw.StartsWith( "NPC_", StringComparison.Ordinal ) ) return CleanPrefixedName( raw, "NPC_", NpcDirect, skipSubstrs: new[] { "Jiggle" } );
		if ( raw.StartsWith( "Elk", StringComparison.Ordinal ) ) return CleanPrefixedName( raw, "Elk", ElkMap );
		if ( raw.StartsWith( "jt_", StringComparison.Ordinal ) ) return CleanPrefixedName( raw, "jt_", JtMap, stripTrailingC: true );
		var baseLower = Regex.Replace( raw, @"^[RL]_", "", RegexOptions.None, TimeSpan.FromSeconds( 1 ) ).ToLowerInvariant();
		baseLower = Regex.Replace( baseLower, @"\d+$", "" );
		if ( JapaneseWordsLower.ContainsKey( baseLower ) ) return CleanJapaneseName( raw );
		if ( JapaneseCompoundAnimals.Contains( animal ) && JapaneseCompoundNames.Contains( raw ) ) return CleanJapaneseName( raw );
		return CleanStandardName( raw );
	}

	/// <summary>Upstream <c>post_process(name)</c>.</summary>
	public static string PostProcess( string name )
	{
		name = PyStrip( Regex.Replace( name, @"\s+", " " ) ).TrimEnd( '.', '_', ' ' );
		name = Regex.Replace( name, @"\s+\d+$", "" );
		name = Regex.Replace( name, @"(\w)\d+\s+End", "$1 End" );
		name = Regex.Replace( name, @"\bHand (Thumb|Index|Middle|Ring|Pinky) Finger\b", "$1 Finger" );
		name = Regex.Replace( name, @"\b(Thumb|Index|Middle|Ring|Pinky) Finger Finger\b", "$1 Finger" );
		name = Regex.Replace( name, @"\bFinger (Thumb|Index|Middle|Ring|Pinky) Finger\b", "$1 Finger" );
		name = Regex.Replace( name, @"\b(?:Up|Upper) Leg$", "Thigh" );
		name = Regex.Replace( name, @"\bLower Leg$", "Shin" );
		name = Regex.Replace( name, @"\b(?:Lower|Fore) Arm$", "Forearm" );
		name = Regex.Replace( name, @"\bToe Base$", "Toe" );
		name = Regex.Replace( name, @"^(?!Left |Right )(.+) (Left|Right)$", "$2 $1" );
		name = Regex.Replace( name, @"^Head Top End$", "Head End" );
		name = name.Replace( ". ", " " );
		name = Regex.Replace( name, @"^Spine ?\d* ?Tail$", "Tail" );
		name = Regex.Replace( name, @"^Head ?\d* ?Jaw$", "Jaw" );
		name = Regex.Replace( name, @"^Head ?\d* ?Eyelid$", "Eyelid" );
		name = Regex.Replace( name, @"^Head Muzzle$", "Muzzle" );
		name = Regex.Replace( name, @"^Head Jaw End$", "Jaw End" );
		name = Regex.Replace( name, @"^Head Brain$", "Head" );
		name = Regex.Replace( name, @"Bip \d+ ", "" );
		name = Regex.Replace( name, @"^(\w+) \1$", "$1" );
		if ( Regex.IsMatch( name, "^Xtra" ) ) name = "Bone";
		name = Regex.Replace( name, @"Ponytail\d*.*", "Appendage" );
		var words = name.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );
		name = string.Join( " ", words.Select( w => char.IsLower( w[0] ) && w.Length > 1 ? Capitalize( w ) : w ) );
		name = Regex.Replace( name, @"^Spine (Left|Right) Wing$", "$1 Wing" );
		name = Regex.Replace( name, @"\s+\d+$", "" );
		return name.Length == 0 ? "Bone" : name;
	}

	/// <summary>Clean label for a raw bone name, as <c>names_clean_rule.py</c> writes it.</summary>
	public static string Clean( string raw, string animal ) => PostProcess( CleanJointName( raw, animal ) );

	// =====================================================================================================
	// face_select_rule.py
	// =====================================================================================================

	static readonly HashSet<string> TailKeywords = new() { "Tail", "Tail Twist" };
	static readonly HashSet<string> HeadKeywords = new() { "Head", "Skull", "Skull Base", "Head End", "Jaw", "Upper Jaw", "Lower Jaw", "Tongue", "Muzzle", "Nose", "Chin" };

	static readonly string[] SymmetricPairPriority =
	{
		"Thigh", "Shoulder", "Front Shoulder", "Back Hip", "Hip", "Scapula",
		"Upper Arm", "Arm", "Front Leg", "Hind Leg", "Middle Leg", "Back Leg", "Wing", "Leg",
		"Pectoral Fin", "Pelvic Fin", "Fin", "Gill", "Pincer", "Mandible", "Large Mandible", "Lower Mandible", "Stinger", "Claw",
		"Hand Claw", "Antenna",
		"Forearm", "Shin", "Knee", "Elbow", "Ankle", "Wrist",
		"Hand", "Palm", "Foot", "Heel", "Front Paw", "Back Paw", "Paw", "Front Hoof", "Rear Hoof", "Hoof", "Fetlock", "Cannon",
		"Metacarpus", "Pastern", "Toe",
		"Thumb Finger", "Index Finger", "Middle Finger", "Ring Finger", "Pinky Finger", "Finger",
		"Neck",
		"Eye", "Eyeball", "Eyelid", "Eyebrow", "Ear", "Horn", "Cheek", "Whisker", "Fang", "Barbel", "Tentacle", "Feather",
		"Tail",
	};

	static string StripTrailingNum( string name ) => Regex.Replace( name, @"\s+\d+$", "" );

	static string[] DigitRuns( string raw ) => Regex.Matches( raw, @"\d+" ).Select( m => m.Value ).ToArray();

	static (int R, int L) MatchPairBySuffix( List<int> r, List<int> l, IReadOnlyList<string> raw )
	{
		if ( r.Count == 1 && l.Count == 1 ) return (r[0], l[0]);
		foreach ( var dropLast in new[] { false, true } )
		{
			string Key( int i ) { var runs = DigitRuns( raw[i] ); if ( dropLast && runs.Length > 0 ) runs = runs[..^1]; return string.Join( "|", runs ) + "#" + runs.Length; }
			var bySig = new Dictionary<string, int>();
			foreach ( var li in l ) bySig.TryAdd( Key( li ), li );
			foreach ( var ri in r ) if ( bySig.TryGetValue( Key( ri ), out var li ) ) return (ri, li);
		}
		return (r[0], l[0]);
	}

	/// <summary>The facing joints upstream's rule picks: (right, left, bodyAxis), indices into the given lists; (-1, -1) = none.</summary>
	public static (int Right, int Left, bool BodyAxis, string Source) ResolveFaceJoints( IReadOnlyList<string> clean, IReadOnlyList<string> raw )
	{
		var right = new Dictionary<string, List<int>>();
		var left = new Dictionary<string, List<int>>();
		var rightOrder = new List<string>(); var leftOrder = new List<string>();
		for ( var i = 0; i < clean.Count; i++ )
		{
			var n = clean[i];
			if ( n.StartsWith( "Right ", StringComparison.Ordinal ) )
			{
				var k = StripTrailingNum( n[6..] );
				if ( !right.TryGetValue( k, out var list ) ) { right[k] = list = new List<int>(); rightOrder.Add( k ); }
				list.Add( i );
			}
			else if ( n.StartsWith( "Left ", StringComparison.Ordinal ) )
			{
				var k = StripTrailingNum( n[5..] );
				if ( !left.TryGetValue( k, out var list ) ) { left[k] = list = new List<int>(); leftOrder.Add( k ); }
				list.Add( i );
			}
		}
		var common = right.Keys.Where( left.ContainsKey ).ToHashSet();
		if ( common.Count > 0 )
		{
			var ordered = SymmetricPairPriority.Where( common.Contains ).ToList();
			ordered.AddRange( common.Except( SymmetricPairPriority ).OrderBy( s => s, StringComparer.Ordinal ) );
			var suffix = ordered[0];
			var (ri, li) = MatchPairBySuffix( right[suffix], left[suffix], raw );
			return (ri, li, false, suffix.ToLowerInvariant());
		}
		int tail = -1, head = -1;
		for ( var i = 0; i < clean.Count; i++ )
		{
			var b = StripTrailingNum( clean[i] );
			if ( TailKeywords.Contains( b ) ) tail = i;
			if ( HeadKeywords.Contains( b ) ) head = i;
		}
		if ( tail >= 0 && head >= 0 ) return (head, tail, true, "body_axis");
		return (-1, -1, false, "empty");
	}
}

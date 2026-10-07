using System;
using System.Collections.Generic;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>
/// The joint vocabulary UniMate was trained on, and the alignment of a new rig's names to it. Upstream's name rule
/// (<see cref="UniMateNames"/>) turns raw bone names into words, but only words seen in training mean anything to the
/// network's name embeddings: a rule output like "Left Leg Upper" (s&amp;box's leg_upper_L) never occurs in UniMate's
/// data, which says "Left Thigh". Measured on the s&amp;box human, the rule's names leave the arms raised and the
/// shins kicking back; with the training words the walk matches UniMate's own. <see cref="Align"/> rewrites a name
/// only when it is not in the vocabulary and a limb-word synonym of it is; everything else stays as upstream's rule
/// made it.
/// </summary>
public static class UniMateVocabulary
{
	/// <summary>Every clean joint name in UniMate's training data (UniML3D: Mixamo, Truebones and Objaverse cond.npy).</summary>
	public static readonly HashSet<string> Names = new( StringComparer.Ordinal )
	{
		"Abdomen", "Abdomen End", "Anal Fin", "Ankle", "Antenna", "Appendage", "Appendage End", "Back Appendage",
		"Back Body", "Back Bone", "Back Bone End", "Back Center Mane", "Back Claw", "Back Hip", "Back Left Mane",
		"Back Leg", "Back Lower Appendage", "Back Lower Appendage End", "Back Mandible", "Back Right Mane",
		"Back Shoulder", "Back Thigh", "Back Upper Appendage", "Back Upper Appendage End", "Back Waist", "Belly", "Body",
		"Body End", "Bone", "Bone End", "Caudal Fin", "Center", "Cheek", "Chest", "Chest Appendage", "Chin", "Chin End",
		"Crest", "Dorsal Fin", "Dorsal Plate", "Ear", "Elbow", "Eye", "Eye End", "Eyeball", "Eyebrow", "Eyebrow End",
		"Eyelid", "Face", "Feather", "Fin", "Finger", "Foot", "Foot End", "Forearm", "Forehead", "Front Appendage",
		"Front Bone", "Front Bone End", "Front Elbow", "Front Hip", "Front Left Mane", "Front Leg", "Front Leg End",
		"Front Lower Appendage", "Front Lower Appendage End", "Front Mandible", "Front Right Mane", "Front Shoulder",
		"Front Thigh", "Front Upper Appendage", "Front Upper Appendage End", "Front Waist", "Front Whisker", "Fur", "Hand",
		"Hand End", "Handle", "Head", "Head End", "Head Upper End", "Hind Leg", "Hind Leg End", "Hip", "Hip End", "Hips",
		"Horn", "Horn End", "IK Chain", "Index Finger", "Index Finger End", "Inner Tail", "Jaw", "Jaw End", "Knee",
		"Left Ankle", "Left Ankle End", "Left Antenna", "Left Appendage", "Left Appendage End", "Left Back Ankle",
		"Left Back Appendage", "Left Back Claw", "Left Back Feather", "Left Back Feather End", "Left Back Foot",
		"Left Back Foot End", "Left Back Hip", "Left Back Hoof", "Left Back Hoof End", "Left Back Inner Toe",
		"Left Back Inner Toe End", "Left Back Knee", "Left Back Leg", "Left Back Lower Appendage",
		"Left Back Middle Finger", "Left Back Outer Toe", "Left Back Outer Toe End", "Left Back Paw", "Left Back Paw End",
		"Left Back Shin", "Left Back Shin End", "Left Back Thigh", "Left Back Toe", "Left Back Upper Arm",
		"Left Back Wing", "Left Back Wing End", "Left Body", "Left Bone", "Left Bone End", "Left Cannon", "Left Cheek",
		"Left Chest", "Left Chest End", "Left Chin", "Left Claw", "Left Claw End", "Left Collar", "Left Collar Appendage",
		"Left Collar End", "Left Ear", "Left Ear End", "Left Elbow", "Left Elbow Back", "Left Elbow End",
		"Left Elbow Twist", "Left Eye", "Left Eye End", "Left Eyeball", "Left Eyeball End", "Left Eyebrow", "Left Eyelid",
		"Left Fang", "Left Feather", "Left Feather End", "Left Fetlock", "Left Fin", "Left Fin End", "Left Finger",
		"Left Finger End", "Left Foot", "Left Foot End", "Left Forearm", "Left Forearm Appendage", "Left Forearm End",
		"Left Forearm IK Chain", "Left Forearm IK Chain End", "Left Forearm Twist", "Left Forearm Twist End",
		"Left Front Ankle", "Left Front Appendage", "Left Front Bone", "Left Front Elbow", "Left Front Finger",
		"Left Front Foot", "Left Front Foot End", "Left Front Forearm", "Left Front Hip", "Left Front Hoof",
		"Left Front Hoof End", "Left Front Index Finger", "Left Front Inner Toe", "Left Front Inner Toe End",
		"Left Front Knee", "Left Front Leg", "Left Front Leg Ankle", "Left Front Leg Bone", "Left Front Leg End",
		"Left Front Leg Hip", "Left Front Leg Knee", "Left Front Leg Palm", "Left Front Leg Toe", "Left Front Leg Toe End",
		"Left Front Lower Appendage", "Left Front Middle Finger", "Left Front Middle Leg", "Left Front Middle Leg Palm",
		"Left Front Middle Wing", "Left Front Outer Toe", "Left Front Outer Toe End", "Left Front Palm", "Left Front Paw",
		"Left Front Paw End", "Left Front Pinky Finger", "Left Front Scapula", "Left Front Shin", "Left Front Shin End",
		"Left Front Shoulder", "Left Front Thigh", "Left Front Toe", "Left Front Toe End", "Left Front Upper Arm",
		"Left Front Wing", "Left Front Wing End", "Left Front Wrist", "Left Fur", "Left Gill", "Left Halter", "Left Hand",
		"Left Hand Appendage", "Left Hand End", "Left Head", "Left Heel", "Left Heel End", "Left Heel Toe",
		"Left Hind Ankle", "Left Hind Foot", "Left Hind Foot End", "Left Hind Hip", "Left Hind Knee", "Left Hind Leg",
		"Left Hind Leg Ankle", "Left Hind Leg End", "Left Hind Leg Hip", "Left Hind Leg Knee", "Left Hind Leg Palm",
		"Left Hind Leg Shin", "Left Hind Leg Thigh", "Left Hind Leg Toe", "Left Hind Leg Toe End", "Left Hind Middle Leg",
		"Left Hind Middle Leg Palm", "Left Hind Shin", "Left Hind Thigh", "Left Hind Toe", "Left Hip", "Left Hips",
		"Left Hoof", "Left Hoof End", "Left Horn", "Left Horn End", "Left Index Finger", "Left Index Finger End",
		"Left Index Toe", "Left Inner Claw", "Left Inner Finger", "Left Inner Hand Claw", "Left Inner Toe", "Left Jaw",
		"Left Knee", "Left Knee End", "Left Knee Twist", "Left Large Mandible", "Left Leg", "Left Leg End", "Left Lip",
		"Left Lip End", "Left Long Toe", "Left Lower Antenna", "Left Lower Appendage", "Left Lower Appendage End",
		"Left Lower Claw", "Left Lower Eye", "Left Lower Eyebrow", "Left Lower Eyelid", "Left Lower Eyelid End",
		"Left Lower Finger", "Left Lower Finger End", "Left Lower Foot", "Left Lower Lip", "Left Lower Lip End",
		"Left Lower Mandible", "Left Lower Pincer", "Left Lower Thumb", "Left Lower Wing", "Left Mandible",
		"Left Mandible End", "Left Mane", "Left Metacarpus", "Left Middle Ankle", "Left Middle Appendage",
		"Left Middle Back Leg", "Left Middle Claw", "Left Middle Ear", "Left Middle Finger", "Left Middle Finger End",
		"Left Middle Foot", "Left Middle Front Leg", "Left Middle Hand Claw", "Left Middle Hip", "Left Middle Knee",
		"Left Middle Leg", "Left Middle Leg End", "Left Middle Leg Shin", "Left Middle Leg Thigh", "Left Middle Shin",
		"Left Middle Thigh", "Left Middle Toe", "Left Middle Wing", "Left Middle Wing End", "Left Mouth", "Left Muzzle",
		"Left Neck", "Left Nose", "Left Outer Claw", "Left Outer Finger", "Left Outer Hand Claw", "Left Outer Toe",
		"Left Palm", "Left Pastern", "Left Paw", "Left Paw End", "Left Pectoral Fin", "Left Pectoral Fin End",
		"Left Pelvic Fin", "Left Pelvic Fin End", "Left Pelvis", "Left Pelvis End", "Left Phalanges", "Left Phalanges End",
		"Left Pincer", "Left Pinky Finger", "Left Pinky Finger End", "Left Pinky Toe", "Left Pupil", "Left Rear Hoof",
		"Left Rear Index Finger", "Left Rear Middle Finger", "Left Rear Pinky Finger", "Left Rear Shin", "Left Rear Thigh",
		"Left Rear Toe", "Left Ribcage", "Left Ring Finger", "Left Ring Finger End", "Left Ring Toe", "Left Root",
		"Left Root End", "Left Scapula", "Left Shell", "Left Shin", "Left Shin End", "Left Shin IK Chain",
		"Left Shin IK Chain End", "Left Shin Twist", "Left Shoulder", "Left Shoulder End", "Left Shoulder Muscle",
		"Left Shoulder Twist", "Left Spine", "Left Tail", "Left Tentacle", "Left Thigh", "Left Thigh IK Chain",
		"Left Thigh Jiggle", "Left Thigh Muscle", "Left Thigh Twist", "Left Thigh Twist End", "Left Thumb",
		"Left Thumb Finger", "Left Thumb Finger End", "Left Toe", "Left Toe End", "Left Twist", "Left Upper Antenna",
		"Left Upper Arm", "Left Upper Arm End", "Left Upper Arm IK Chain", "Left Upper Arm Jiggle", "Left Upper Arm Twist",
		"Left Upper Arm Twist End", "Left Upper Claw", "Left Upper Eyebrow", "Left Upper Eyelid", "Left Upper Eyelid End",
		"Left Upper Hip", "Left Upper Lip", "Left Upper Lip End", "Left Upper Nose", "Left Upper Pincer",
		"Left Upper Wing", "Left Waist", "Left Whisker", "Left Wing", "Left Wing Elbow", "Left Wing End",
		"Left Wing Feather", "Left Wing Feather End", "Left Wing Forearm", "Left Wing Hand", "Left Wing Shell",
		"Left Wing Shoulder", "Left Wing Upper Arm", "Left Wrist", "Left Wrist Back", "Left Wrist End", "Leg", "Lip",
		"Lower Back", "Lower Belly", "Lower Body", "Lower Chest", "Lower Eyelid", "Lower Front Lip", "Lower Head",
		"Lower Jaw", "Lower Jaw End", "Lower Left Lip", "Lower Lip", "Lower Lip End", "Lower Mouth", "Lower Neck",
		"Lower Right Lip", "Lower Spine", "Mane", "Mane End", "Middle Appendage", "Middle Appendage End", "Middle Back",
		"Middle Back Feather", "Middle Back Feather End", "Middle Body", "Middle Claw", "Middle Finger",
		"Middle Finger End", "Middle Head", "Middle Neck", "Middle Spine", "Middle Tail", "Mouth", "Mouth End", "Muzzle",
		"Muzzle End", "Neck", "Neck End", "Neck Jiggle", "Neck Muscle", "Nose", "Nose End", "Outer Tail", "Outer Tail End",
		"Palm", "Pelvis Appendage", "Pinky Finger", "Pinky Finger End", "Reins", "Rib", "Ribcage", "Right Ankle",
		"Right Ankle End", "Right Antenna", "Right Appendage", "Right Appendage End", "Right Back Ankle",
		"Right Back Appendage", "Right Back Claw", "Right Back Feather", "Right Back Feather End", "Right Back Foot",
		"Right Back Foot End", "Right Back Hip", "Right Back Hoof", "Right Back Hoof End", "Right Back Inner Toe",
		"Right Back Inner Toe End", "Right Back Knee", "Right Back Leg", "Right Back Lower Appendage",
		"Right Back Outer Toe", "Right Back Outer Toe End", "Right Back Paw", "Right Back Paw End", "Right Back Shin",
		"Right Back Shin End", "Right Back Thigh", "Right Back Toe", "Right Back Toe End", "Right Back Upper Arm",
		"Right Back Wing", "Right Body", "Right Bone", "Right Bone End", "Right Cannon", "Right Cheek", "Right Chest",
		"Right Chest End", "Right Chin", "Right Claw", "Right Claw End", "Right Collar", "Right Collar Appendage",
		"Right Collar End", "Right Ear", "Right Ear End", "Right Elbow", "Right Elbow Back", "Right Elbow End",
		"Right Elbow Twist", "Right Eye", "Right Eye End", "Right Eyeball", "Right Eyeball End", "Right Eyebrow",
		"Right Eyelid", "Right Fang", "Right Feather", "Right Feather End", "Right Fetlock", "Right Fin", "Right Fin End",
		"Right Finger", "Right Finger End", "Right Foot", "Right Foot End", "Right Forearm", "Right Forearm Appendage",
		"Right Forearm End", "Right Forearm IK Chain", "Right Forearm IK Chain End", "Right Forearm Twist",
		"Right Forearm Twist End", "Right Front Ankle", "Right Front Appendage", "Right Front Bone", "Right Front Collar",
		"Right Front Elbow", "Right Front Finger", "Right Front Foot", "Right Front Foot End", "Right Front Forearm",
		"Right Front Hip", "Right Front Hoof", "Right Front Hoof End", "Right Front Index Finger", "Right Front Inner Toe",
		"Right Front Inner Toe End", "Right Front Knee", "Right Front Leg", "Right Front Leg Ankle",
		"Right Front Leg Bone", "Right Front Leg End", "Right Front Leg Hip", "Right Front Leg Knee",
		"Right Front Leg Palm", "Right Front Leg Toe", "Right Front Leg Toe End", "Right Front Lower Appendage",
		"Right Front Middle Finger", "Right Front Middle Leg", "Right Front Middle Leg Palm", "Right Front Middle Wing",
		"Right Front Outer Toe", "Right Front Outer Toe End", "Right Front Palm", "Right Front Paw", "Right Front Paw End",
		"Right Front Pinky Finger", "Right Front Scapula", "Right Front Shin", "Right Front Shin End",
		"Right Front Shoulder", "Right Front Thigh", "Right Front Toe", "Right Front Toe End", "Right Front Upper Arm",
		"Right Front Wing", "Right Front Wing End", "Right Front Wrist", "Right Fur", "Right Gill", "Right Halter",
		"Right Hand", "Right Hand Appendage", "Right Hand End", "Right Head", "Right Heel", "Right Heel End",
		"Right Heel Toe", "Right Hind Ankle", "Right Hind Foot", "Right Hind Foot End", "Right Hind Hip",
		"Right Hind Knee", "Right Hind Leg", "Right Hind Leg Ankle", "Right Hind Leg End", "Right Hind Leg Hip",
		"Right Hind Leg Knee", "Right Hind Leg Palm", "Right Hind Leg Shin", "Right Hind Leg Thigh", "Right Hind Leg Toe",
		"Right Hind Leg Toe End", "Right Hind Middle Leg", "Right Hind Middle Leg Palm", "Right Hind Shin",
		"Right Hind Thigh", "Right Hind Toe", "Right Hip", "Right Hips", "Right Hoof", "Right Hoof End", "Right Horn",
		"Right Horn End", "Right Index Finger", "Right Index Finger End", "Right Index Toe", "Right Inner Claw",
		"Right Inner Finger", "Right Inner Hand Claw", "Right Inner Toe", "Right Jaw", "Right Knee", "Right Knee End",
		"Right Knee Twist", "Right Large Mandible", "Right Large Mandible End", "Right Leg", "Right Leg End", "Right Lip",
		"Right Lip End", "Right Long Toe", "Right Lower Antenna", "Right Lower Appendage", "Right Lower Appendage End",
		"Right Lower Claw", "Right Lower Eye", "Right Lower Eyebrow", "Right Lower Eyelid", "Right Lower Eyelid End",
		"Right Lower Foot", "Right Lower Lip", "Right Lower Lip End", "Right Lower Mandible", "Right Lower Pincer",
		"Right Lower Wing", "Right Mandible", "Right Mandible End", "Right Mane", "Right Metacarpus", "Right Middle Ankle",
		"Right Middle Back Leg", "Right Middle Claw", "Right Middle Ear", "Right Middle Finger", "Right Middle Finger End",
		"Right Middle Foot", "Right Middle Front Leg", "Right Middle Hand Claw", "Right Middle Hip", "Right Middle Knee",
		"Right Middle Leg", "Right Middle Leg End", "Right Middle Leg Shin", "Right Middle Leg Thigh", "Right Middle Shin",
		"Right Middle Thigh", "Right Middle Toe", "Right Middle Wing", "Right Middle Wing End", "Right Mouth",
		"Right Muzzle", "Right Neck", "Right Nose", "Right Outer Claw", "Right Outer Finger", "Right Outer Hand Claw",
		"Right Outer Toe", "Right Palm", "Right Pastern", "Right Paw", "Right Paw End", "Right Pectoral Fin",
		"Right Pectoral Fin End", "Right Pelvic Fin", "Right Pelvic Fin End", "Right Pelvis", "Right Pelvis End",
		"Right Phalanges", "Right Phalanges End", "Right Pincer", "Right Pinky Finger", "Right Pinky Finger End",
		"Right Pinky Toe", "Right Pupil", "Right Rear Hip", "Right Rear Hoof", "Right Rear Index Finger",
		"Right Rear Middle Finger", "Right Rear Pinky Finger", "Right Rear Shin", "Right Rear Thigh", "Right Rear Toe",
		"Right Ribcage", "Right Ring Finger", "Right Ring Finger End", "Right Ring Toe", "Right Root", "Right Root End",
		"Right Scapula", "Right Shell", "Right Shin", "Right Shin End", "Right Shin IK Chain", "Right Shin IK Chain End",
		"Right Shin Twist", "Right Shoulder", "Right Shoulder End", "Right Shoulder Muscle", "Right Shoulder Twist",
		"Right Spine", "Right Tail", "Right Tentacle", "Right Thigh", "Right Thigh IK Chain", "Right Thigh Jiggle",
		"Right Thigh Muscle", "Right Thigh Twist", "Right Thigh Twist End", "Right Thumb", "Right Thumb Finger",
		"Right Thumb Finger End", "Right Toe", "Right Toe End", "Right Twist", "Right Upper Antenna",
		"Right Upper Appendage", "Right Upper Arm", "Right Upper Arm End", "Right Upper Arm IK Chain",
		"Right Upper Arm Jiggle", "Right Upper Arm Twist", "Right Upper Arm Twist End", "Right Upper Claw",
		"Right Upper Eyebrow", "Right Upper Eyelid", "Right Upper Eyelid End", "Right Upper Hip", "Right Upper Lip",
		"Right Upper Lip End", "Right Upper Nose", "Right Upper Pincer", "Right Upper Wing", "Right Waist",
		"Right Whisker", "Right Wing", "Right Wing Elbow", "Right Wing End", "Right Wing Feather",
		"Right Wing Feather End", "Right Wing Forearm", "Right Wing Hand", "Right Wing Shell", "Right Wing Shoulder",
		"Right Wing Upper Arm", "Right Wrist", "Right Wrist Back", "Right Wrist End", "Ring Finger", "Ring Finger End",
		"Root", "Root End", "Scapula", "Shell", "Shin", "Shin End", "Shoulder", "Skull", "Skull Base", "Spine",
		"Spine End", "Spine Jiggle", "Tail", "Tail End", "Tentacle", "Tentacle End", "Thigh", "Thumb Finger",
		"Thumb Finger End", "Toe", "Tongue", "Tongue End", "Upper Arm", "Upper Belly", "Upper Body", "Upper Chest",
		"Upper Chin", "Upper Eyelid", "Upper Fin", "Upper Head", "Upper Head End", "Upper Jaw", "Upper Jaw End",
		"Upper Left Lip", "Upper Lip", "Upper Mouth", "Upper Neck", "Upper Nose", "Upper Right Lip", "Upper Spine",
		"Waist", "Waist End", "Whisker", "Wing", "Wrist",
	};

	/// <summary>Limb words in the order the training data writes them (whole words, applied left to right).</summary>
	static readonly (string From, string To)[] Synonyms =
	{
		("Leg Upper", "Thigh"), ("Upper Leg", "Thigh"),
		("Leg Lower", "Shin"), ("Lower Leg", "Shin"),
		("Arm Upper", "Upper Arm"),
		("Arm Lower", "Forearm"), ("Lower Arm", "Forearm"),
		("Pelvis", "Hips"),
	};

	/// <summary>The training-vocabulary form of a clean joint name (unchanged when it is already in it or no synonym is).</summary>
	public static string Align( string clean )
	{
		if ( string.IsNullOrEmpty( clean ) || Names.Contains( clean ) ) return clean;
		var padded = " " + clean + " ";
		foreach ( var (from, to) in Synonyms )
			padded = padded.Replace( " " + from + " ", " " + to + " ", StringComparison.Ordinal );
		var aligned = padded.Trim();
		return aligned != clean && Names.Contains( aligned ) ? aligned : clean;
	}
}

// Vendored from humanoid-retargeter (FbxBindPoseFixer.QuaternionToEulerDegrees).
using System;
using System.Numerics;

namespace TextToAnimation.EditorTools.Formats.Fbx;

using Vector3 = System.Numerics.Vector3;

/// <summary>Quaternion to FBX euler angles, the inverse of <see cref="FbxTransform.EulerDegreesToQuaternion"/>.</summary>
public static class FbxEuler
{

    /// <summary>
    /// Decomposes a quaternion into FBX euler degrees for the given RotationOrder, the
    /// exact inverse of <see cref="FbxTransform.EulerDegreesToQuaternion"/>. Tait-Bryan
    /// extraction on the column-convention rotation matrix.
    /// </summary>
    public static Vector3 QuaternionToEulerDegrees(Quaternion q, int order)
    {
        // Column-convention matrix C (v' = C·v): C = transpose of System.Numerics' row form.
        var m = Matrix4x4.CreateFromQuaternion(q);
        // C[r,c]: row r, column c.
        float c00 = m.M11, c01 = m.M21, c02 = m.M31;
        float c10 = m.M12, c11 = m.M22, c12 = m.M32;
        float c20 = m.M13, c21 = m.M23, c22 = m.M33;

        const float radToDeg = 180f / MathF.PI;
        float a, b, c;
        switch (order)
        {
            case 0: // XYZ: C = Rz·Ry·Rx
                b = MathF.Asin(Math.Clamp(-c20, -1f, 1f));
                a = MathF.Atan2(c21, c22);
                c = MathF.Atan2(c10, c00);
                return new Vector3(a * radToDeg, b * radToDeg, c * radToDeg);
            case 1: // XZY: C = Ry·Rz·Rx
                b = MathF.Asin(Math.Clamp(c10, -1f, 1f));
                a = MathF.Atan2(-c12, c11);
                c = MathF.Atan2(-c20, c00);
                return new Vector3(a * radToDeg, c * radToDeg, b * radToDeg);
            case 2: // YZX: C = Rx·Rz·Ry
                b = MathF.Asin(Math.Clamp(-c01, -1f, 1f));
                a = MathF.Atan2(c02, c00);
                c = MathF.Atan2(c21, c11);
                return new Vector3(c * radToDeg, a * radToDeg, b * radToDeg);
            case 3: // YXZ: C = Rz·Rx·Ry
                b = MathF.Asin(Math.Clamp(c21, -1f, 1f));
                a = MathF.Atan2(-c20, c22);
                c = MathF.Atan2(-c01, c11);
                return new Vector3(b * radToDeg, a * radToDeg, c * radToDeg);
            case 4: // ZXY: C = Ry·Rx·Rz
                b = MathF.Asin(Math.Clamp(-c12, -1f, 1f));
                a = MathF.Atan2(c02, c22);
                c = MathF.Atan2(c10, c11);
                return new Vector3(b * radToDeg, a * radToDeg, c * radToDeg);
            case 5: // ZYX: C = Rx·Ry·Rz
            case 6: // eSphericXYZ treated as XYZ on read; mirror that here
            default:
                if (order == 5)
                {
                    b = MathF.Asin(Math.Clamp(c02, -1f, 1f));
                    a = MathF.Atan2(-c01, c00);
                    c = MathF.Atan2(-c12, c22);
                    return new Vector3(c * radToDeg, b * radToDeg, a * radToDeg);
                }
                goto case 0;
        }
    }
}

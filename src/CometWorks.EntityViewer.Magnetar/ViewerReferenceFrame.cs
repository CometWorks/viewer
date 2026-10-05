using VRageMath;

namespace CometWorks.EntityViewer.Magnetar
{
    internal static class ViewerReferenceFrame
    {
        public static MatrixD? GravityAligned(MatrixD gridFrame, Vector3 gravity)
        {
            if (!gravity.IsValid() || gravity.LengthSquared() == 0)
                return null;

            var up = Vector3D.Normalize(-(Vector3D)gravity);
            var forward = gridFrame.Forward - up * Vector3D.Dot(gridFrame.Forward, up);
            // A vertical grid forward has no horizontal heading; retain its right axis instead.
            if (forward.LengthSquared() < 0.0001)
                forward = Vector3D.Cross(up, gridFrame.Right);

            return MatrixD.CreateWorld(gridFrame.Translation, Vector3D.Normalize(forward), up);
        }
    }
}

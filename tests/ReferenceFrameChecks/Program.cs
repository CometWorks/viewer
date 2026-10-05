using CometWorks.EntityViewer.Magnetar;
using VRageMath;

var center = new Vector3D(1000000, -2000000, 3000000);
var grid = MatrixD.CreateFromYawPitchRoll(0.4, 0.7, 1.1);
grid.Translation = center;

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void CheckFrame(MatrixD gridFrame, Vector3 gravity)
{
    var frame = ViewerReferenceFrame.GravityAligned(gridFrame, gravity)
        ?? throw new InvalidOperationException("Nonzero natural gravity must produce a view frame.");
    var inverse = MatrixD.Invert(frame);
    Check(Vector3D.Distance(frame.Up, Vector3D.Normalize(-(Vector3D)gravity)) < 1e-10, "Viewer up must oppose gravity.");
    Check(Vector3D.Distance(frame.Translation, center) < 1e-10, "Alignment must preserve the scene center.");
    Check(Math.Abs(frame.Determinant() - 1) < 1e-10, "Alignment must be a finite right-handed rigid rotation.");
    Check(Vector3D.Transform(center, inverse).Length() < 1e-8, "Scene center must map to the view origin.");
    var relativeGravity = Vector3D.TransformNormal(Vector3D.Normalize(gravity), inverse);
    Check(Vector3D.Distance(relativeGravity, Vector3D.Down) < 1e-10, "Gravity must point down in viewer coordinates.");

    var projectedForward = gridFrame.Forward - frame.Up * Vector3D.Dot(gridFrame.Forward, frame.Up);
    if (projectedForward.LengthSquared() >= 0.0001)
        Check(Vector3D.Dot(frame.Forward, Vector3D.Normalize(projectedForward)) > 1 - 1e-10, "Alignment must retain the grid's horizontal heading.");
    else
        Check(Vector3D.Dot(frame.Right, gridFrame.Right) > 1 - 1e-10, "Vertical heading must retain the grid's right axis.");

    var localPoint = new Vector3D(12, -4, 7);
    var worldPoint = Vector3D.Transform(localPoint, gridFrame);
    var gridToView = gridFrame * inverse;
    Check(Vector3D.Distance(Vector3D.Transform(localPoint, gridToView), Vector3D.Transform(worldPoint, inverse)) < 1e-8,
        "Grid geometry and world-space terrain must share the same view coordinates.");
    Check(Vector3D.Distance(Vector3D.Transform(Vector3D.Transform(localPoint, gridToView), frame), worldPoint) < 1e-8,
        "View clipping coordinates must round-trip to world coordinates.");
}

foreach (var gravity in new[] { Vector3.Down * 9.81f, Vector3.Up, Vector3.Right, new Vector3(3, -7, 2), Vector3.Down * 1e-6f })
    CheckFrame(grid, gravity);

foreach (var forward in new[] { Vector3D.Up, Vector3D.Down })
{
    var verticalGrid = MatrixD.CreateWorld(center, forward, Vector3D.Forward);
    CheckFrame(verticalGrid, Vector3.Down);
}

foreach (var gravity in new[] { Vector3.Zero, new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0) })
    Check(!ViewerReferenceFrame.GravityAligned(grid, gravity).HasValue, "Absent or invalid natural gravity must preserve grid alignment.");

Console.WriteLine("Gravity reference frame checks passed.");

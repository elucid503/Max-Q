using System;

using MaxQ.Game.Map;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels;

using UnityEngine;
using UnityEngine.InputSystem;

namespace MaxQ.Game.Vessels.Flight;

/// <summary>Orbits the vessel's centre of mass in its local horizon frame, so the horizon stays level whichever way the
/// vessel turns: the right mouse button swings it round, the wheel draws it in or out.</summary>
public sealed class ChaseCamera {

    private const float LookDegreesPerPixel = 0.15f;
    private const float FieldOfView = 55.0f;
    private const double ZoomPerNotch = 1.12;
    // Close enough to fill the view, far enough that no part reaches the near plane the horizon allows.
    private const double MinimumDistance = 20.0;
    private const double MaximumDistance = 3_000.0;

    // Matches the free camera: past this far-to-near ratio the sun's cascades fall apart.
    private const double DepthRange = 1_000_000.0;

    private readonly Camera _camera;

    private double _heading = 200.0;
    private double _pitch = 12.0;
    private double _distance = 45.0;

    public ChaseCamera(Camera camera) => _camera = camera;

    /// <summary>Sets the view: <paramref name="heading"/> degrees round from looking along the vessel's horizontal motion,
    /// <paramref name="pitch"/> degrees looking down on it, from <paramref name="distance"/> metres.</summary>
    public void Aim(double heading, double pitch, double distance) {

        _heading = heading;
        _pitch = Math.Clamp(pitch, -89.0, 89.0);
        _distance = Math.Clamp(distance, MinimumDistance, MaximumDistance);

    }

    /// <summary>Metres from the vessel's centre of mass.</summary>
    public double Distance => _distance;

    /// <summary>Where the camera is, relative to the root body.</summary>
    public Vector3d Position { get; private set; }

    public void Update(Vessel vessel) {

        Mouse mouse = Mouse.current;

        if (mouse != null) {

            if (mouse.rightButton.isPressed) {

                Vector2 delta = mouse.delta.ReadValue();

                // As the free camera: the view turns the way the mouse moves across, and tips against it up and down.
                Aim(_heading - delta.x * LookDegreesPerPixel, _pitch + delta.y * LookDegreesPerPixel, _distance);

            }

            float scroll = mouse.scroll.ReadValue().y;

            if (scroll != 0.0f) {

                Aim(_heading, _pitch, _distance * Math.Pow(ZoomPerNotch, -Math.Sign(scroll)));

            }

        }

        Vector3d up = vessel.State.Position.Normalized;
        Vector3d velocity = vessel.State.Velocity;
        Vector3d along = velocity - up * Vector3d.Dot(velocity, up);

        along = along.Length > 1e-6 ? along.Normalized : Vector3d.Cross(up, Vector3d.UnitZ).Normalized;

        Vector3d side = Vector3d.Cross(up, along);
        double heading = _heading * Math.PI / 180.0;
        double pitch = _pitch * Math.PI / 180.0;
        Vector3d look = (along * Math.Cos(heading) + side * Math.Sin(heading)) * Math.Cos(pitch) - up * Math.Sin(pitch);

        Position = vessel.RootPosition - look * _distance;
        MapSpace.Origin = Position;

        _camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(MapSpace.Direction(look), MapSpace.Direction(up)));

        // The near plane as far out as the vessel allows: every pass that rebuilds rays from depth (air, clouds, motion
        // vectors) loses precision as it closes in. Never so near that the far plane, a depth range beyond, falls short of
        // the horizon of the ground below.
        double radius = vessel.Body.Radius;
        double height = Math.Max((Position - vessel.Body.PositionAt(vessel.Time)).Length - radius, 1.0);
        double horizon = 1.2 * Math.Sqrt(height * (2.0 * radius + height)) + 100_000.0;
        double near = Math.Max(horizon / DepthRange, 0.5 * (_distance - VesselView.Reach));

        _camera.fieldOfView = FieldOfView;
        _camera.nearClipPlane = (float)(near / MapSpace.MetresPerUnit);
        _camera.farClipPlane = (float)(near * DepthRange / MapSpace.MetresPerUnit);

    }

}

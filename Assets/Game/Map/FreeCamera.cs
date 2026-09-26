using System;

using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.InputSystem;

namespace MaxQ.Game.Map;

/// <summary>Flies over a body in its rotating frame, so the ground holds still beneath it. WASD moves along the view, the
/// right mouse button looks, shift speeds up and ctrl slows down; it never goes below the terrain the sim reports.</summary>
public sealed class FreeCamera {

    private const float LookDegreesPerPixel = 0.15f;
    private const double Clearance = 1.5;
    private const float FieldOfView = 60.0f;
    private const double FastFactor = 10.0;
    private const double SlowFactor = 0.1;

    // Unity fits the sun's shadow cascades to the camera frustum and loses them past this far-to-near ratio.
    private const float DepthRange = 1_000_000.0f;

    private readonly Camera _camera;

    private CelestialBody _body;
    private Vector3d _position;
    private double _heading;
    private double _pitch;

    public FreeCamera(Camera camera) => _camera = camera;

    private double Altitude => _position.Length - _body.Radius - Ground(_position / _position.Length);

    /// <summary>Hovers <paramref name="altitude"/> metres above the ground at a place, looking along <paramref name="heading"/>
    /// (degrees clockwise from north), <paramref name="pitch"/> degrees above the horizon.</summary>
    public void Place(CelestialBody body, double latitude, double longitude, double altitude, double heading, double pitch) {

        double lat = latitude * Math.PI / 180.0;
        double lon = longitude * Math.PI / 180.0;
        Vector3d up = new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));

        _body = body;
        _position = up * (body.Radius + Ground(up) + altitude);
        _heading = heading;
        _pitch = pitch;

    }

    public void Update(double time, float deltaSeconds) {

        Keyboard keys = Keyboard.current;
        Mouse mouse = Mouse.current;

        if (mouse != null && mouse.rightButton.isPressed) {

            Vector2 delta = mouse.delta.ReadValue();

            _heading += delta.x * LookDegreesPerPixel;
            _pitch = Math.Clamp(_pitch - delta.y * LookDegreesPerPixel, -89.0, 89.0);

        }

        (Vector3d up, Vector3d east, Vector3d north) = Frame(_position);
        double heading = _heading * Math.PI / 180.0;
        double pitch = _pitch * Math.PI / 180.0;
        Vector3d ahead = north * Math.Cos(heading) + east * Math.Sin(heading);
        Vector3d forward = ahead * Math.Cos(pitch) + up * Math.Sin(pitch);
        Vector3d top = up * Math.Cos(pitch) - ahead * Math.Sin(pitch);
        Vector3d right = Vector3d.Cross(ahead, up);

        if (keys != null) {

            double speed = Math.Max(2.0, Altitude) * (keys.shiftKey.isPressed ? FastFactor : keys.ctrlKey.isPressed ? SlowFactor : 1.0);
            Vector3d move = forward * Axis(keys.wKey.isPressed, keys.sKey.isPressed) + right * Axis(keys.dKey.isPressed, keys.aKey.isPressed);

            _position += move * speed * deltaSeconds;

        }

        Vector3d surface = _position / _position.Length;
        double floor = _body.Radius + Ground(surface) + Clearance;

        if (_position.Length < floor) {

            _position = surface * floor;

        }

        MapSpace.Origin = _body.PositionAt(time) + _body.FromBodyFixed(_position, time);

        _camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.LookRotation(
            MapSpace.Direction(_body.FromBodyFixed(forward, time)), MapSpace.Direction(_body.FromBodyFixed(top, time))));

        double altitude = Math.Max(Altitude, Clearance);

        _camera.fieldOfView = FieldOfView;
        _camera.nearClipPlane = (float)Math.Clamp(altitude * 0.25 / MapSpace.MetresPerUnit, 2e-4, 10.0);
        _camera.farClipPlane = _camera.nearClipPlane * DepthRange;

    }

    private static double Axis(bool positive, bool negative) => (positive ? 1.0 : 0.0) - (negative ? 1.0 : 0.0);

    // Ground or water, whichever is higher.
    private double Ground(Vector3d direction) {

        if (_body?.Terrain is not { } terrain) {

            return 0.0;

        }

        double level = terrain.WaterLevelAt(direction);
        double ground = terrain.HeightAt(direction, 0.0);

        return double.IsNaN(level) ? ground : Math.Max(ground, level);

    }

    private static (Vector3d Up, Vector3d East, Vector3d North) Frame(Vector3d position) {

        Vector3d up = position / position.Length;
        Vector3d east = Vector3d.Cross(Vector3d.UnitZ, up);

        east = east.Length < 1e-9 ? Vector3d.UnitY : east / east.Length;

        return (up, east, Vector3d.Cross(up, east));

    }

}

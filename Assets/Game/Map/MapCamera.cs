using System;

using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.InputSystem;

namespace MaxQ.Game.Map;

/// <summary>Orbits the focused target, which is always the scene origin. Focus changes glide across.</summary>
public sealed class MapCamera {

    private const float OrbitDegreesPerPixel = 0.25f;
    private const float ZoomStep = 1.15f;
    private const float MaxDistanceUnits = 400_000.0f;
    private const double FocusGlideSeconds = 0.8;

    private readonly Camera _camera;

    private float _yaw = 30.0f;
    private float _pitch = 25.0f;
    private float _distance;
    private float _targetDistance;

    private Func<double, Vector3d> _focus;
    private Vector3d _glideFrom;
    private double _glide = 1.0;

    public float MinDistance { get; private set; }

    public MapCamera(Camera camera) => _camera = camera;

    public void Focus(Func<double, Vector3d> position, double radiusMetres, double time, bool glide) {

        _glideFrom = _focus == null ? position(time) : MapSpace.Origin;
        _glide = glide ? 0.0 : 1.0;
        _focus = position;

        MinDistance = (float)(radiusMetres / MapSpace.MetresPerUnit * 1.6);
        _targetDistance = Mathf.Max(MinDistance * 4.0f, 20.0f);

        if (!glide) {

            _distance = _targetDistance;

        }

    }

    public void SetView(float yaw, float pitch, float distance) {

        _yaw = yaw;
        _pitch = pitch;
        _distance = distance;
        _targetDistance = distance;

    }

    public void Update(double time, float deltaSeconds, bool pointerFree) {

        Mouse mouse = Mouse.current;

        if (mouse != null && pointerFree) {

            if (mouse.rightButton.isPressed) {

                Vector2 delta = mouse.delta.ReadValue();

                _yaw += delta.x * OrbitDegreesPerPixel;
                _pitch = Mathf.Clamp(_pitch - delta.y * OrbitDegreesPerPixel, -89.0f, 89.0f);

            }

            float scroll = mouse.scroll.ReadValue().y;

            if (scroll != 0.0f) {

                _targetDistance *= Mathf.Pow(ZoomStep, -Mathf.Sign(scroll));

            }

        }

        _targetDistance = Mathf.Clamp(_targetDistance, MinDistance, MaxDistanceUnits);

        // Exponential zoom easing, frame-rate independent.
        _distance = Mathf.Exp(Mathf.Lerp(Mathf.Log(_distance), Mathf.Log(_targetDistance), 1.0f - Mathf.Exp(-deltaSeconds * 12.0f)));

        _glide = Math.Min(1.0, _glide + deltaSeconds / FocusGlideSeconds);
        double eased = _glide * _glide * (3.0 - 2.0 * _glide);

        MapSpace.Origin = _glideFrom + (_focus(time) - _glideFrom) * eased;

        Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0.0f);

        _camera.transform.SetPositionAndRotation(rotation * new Vector3(0.0f, 0.0f, -_distance), rotation);
        _camera.nearClipPlane = Mathf.Max(0.01f, _distance * 0.01f);
        _camera.farClipPlane = MaxDistanceUnits * 4.0f;

    }

}

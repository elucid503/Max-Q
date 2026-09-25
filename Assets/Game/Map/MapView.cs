using System;
using System.Collections.Generic;

using MaxQ.Game.Map.Overlay;
using MaxQ.Game.Map.Rendering;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Vessels;

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace MaxQ.Game.Map;

/// <summary>The map scene: owns the clock and the vessel, drives prediction, camera, drawing and HUD in order.</summary>
[RequireComponent(typeof(UIDocument))]
public sealed class MapView : MonoBehaviour {

    private static readonly double[] WarpRates = { 1.0, 5.0, 10.0, 50.0, 100.0, 1_000.0, 10_000.0, 100_000.0 };

    private static readonly Color Current = new Color(0.35f, 0.78f, 0.98f);
    private static readonly Color[] Upcoming = { new Color(1.0f, 0.70f, 0.25f), new Color(0.76f, 0.55f, 1.0f), new Color(0.45f, 0.90f, 0.55f) };
    private static readonly Color Planned = new Color(1.0f, 0.54f, 0.24f);
    private static readonly Color BodyOrbit = new Color(0.62f, 0.68f, 0.76f, 0.35f);

    private const double ParkingAltitude = 150_000.0;
    private const int PatchesAhead = 4;

    [SerializeField] private Texture2D[] _terraFaces;
    [SerializeField] private Texture2D[] _seleneFaces;
    [SerializeField] private Material _surfaceMaterial;
    [SerializeField] private Material _lineMaterial;
    [SerializeField] private Material _skyMaterial;
    [SerializeField] private StyleSheet _hudStyle;

    private CelestialBody _terra;
    private CelestialBody _selene;
    private Vessel _vessel;

    private double _time;
    private int _warp;
    private Maneuver? _node;

    private List<Patch> _actual;
    private List<Patch> _planned;
    private Patch _predictedFrom;
    private bool _nodeChanged;

    private readonly List<BodyView> _bodyViews = new List<BodyView>();
    private readonly List<Hud.Marker> _markers = new List<Hud.Marker>();

    private OrbitLines _actualLines;
    private OrbitLines _plannedLines;
    private OrbitLines _bodyOrbitLines;
    private Patch[] _bodyOrbits;
    private MapCamera _mapCamera;
    private Camera _camera;
    private Hud _hud;
    private int _focusIndex;

    private void Awake() {

        (_terra, _selene) = SolarSystem.Create();

        _camera = Camera.main;
        _mapCamera = new MapCamera(_camera);

        _bodyViews.Add(new BodyView(_terra, _terraFaces, _surfaceMaterial, 0.18f));
        _bodyViews.Add(new BodyView(_selene, _seleneFaces, _surfaceMaterial, 0.0f));

        _actualLines = new OrbitLines("Trajectory", _lineMaterial);
        _plannedLines = new OrbitLines("Planned Trajectory", _lineMaterial);
        _bodyOrbitLines = new OrbitLines("Body Orbits", _lineMaterial);
        _bodyOrbits = new[] { new Patch { Body = _terra, Orbit = _selene.Orbit, StartTime = 0.0, EndTime = double.PositiveInfinity } };

        _hud = new Hud(GetComponent<UIDocument>(), _hudStyle, WarpRates.Length);

        RenderSettings.skybox = _skyMaterial;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.025f, 0.028f, 0.035f);

        // Sunlight travels along -X in the sim frame.
        Light sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.6f;
        sun.color = new Color(1.0f, 0.97f, 0.92f);
        sun.transform.rotation = Quaternion.LookRotation(MapSpace.Direction(-Vector3d.UnitX));

        StartScenario();

    }

    private void StartScenario() {

        double r = _terra.Radius + ParkingAltitude;
        Orbit parking = Orbit.FromStateVectors(new Vector3d(r, 0.0, 0.0), new Vector3d(0.0, Math.Sqrt(_terra.Mu / r), 0.0), _terra.Mu, 0.0);

        _time = 0.0;
        _warp = 0;
        _vessel = new Vessel("Pathfinder", _terra, parking, _time);
        _node = PlanSeleneTransfer();
        _nodeChanged = true;
        _focusIndex = 0;

        FocusOn(0, glide: false);

    }

    private void Update() {

        HandleInput(Time.unscaledDeltaTime);
        AdvanceClock(Time.unscaledDeltaTime * WarpRates[_warp]);
        Predict();

        _mapCamera.Update(_time, Time.unscaledDeltaTime, pointerFree: true);

        foreach (BodyView view in _bodyViews) {

            view.Draw(_time);

        }

        _bodyOrbitLines.Draw(_bodyOrbits, _time, _ => BodyOrbit);

        if (_node.HasValue) {

            _actualLines.Draw(_actual, _time, i => Dim(i == 0 ? Current : Upcoming[(i - 1) % Upcoming.Length]));
            _plannedLines.Draw(_planned, _time, i => _planned[i].StartTime >= _node.Value.Time ? Planned : i == 0 ? Current : Upcoming[(i - 1) % Upcoming.Length]);

        } else {

            _actualLines.Draw(_actual, _time, i => i == 0 ? Current : Upcoming[(i - 1) % Upcoming.Length]);
            _plannedLines.Draw(Array.Empty<Patch>(), _time, _ => Planned);

        }

        DrawHud();

    }

    /// <summary>Capture hook: advances the clock, stopping at the next burn or impact like warp does.</summary>
    internal void Jump(double seconds) => AdvanceClock(seconds);

    internal double TimeToNextEvent => Math.Min(_node?.Time ?? double.PositiveInfinity, _vessel.Current.EndTime) - _time;

    internal void Frame(int focus, float yaw, float pitch, float distance) {

        FocusOn(focus, glide: false);
        _mapCamera.SetView(yaw, pitch, distance);

    }

    private static Color Dim(Color color) => new Color(color.r, color.g, color.b, 0.3f);

    private void HandleInput(float dt) {

        Keyboard keys = Keyboard.current;

        if (keys == null) {

            return;

        }

        if (keys.tabKey.wasPressedThisFrame) {

            FocusOn((_focusIndex + 1) % 3, glide: true);

        }

        if (keys.periodKey.wasPressedThisFrame) {

            _warp = Math.Min(_warp + 1, WarpRates.Length - 1);

        }

        if (keys.commaKey.wasPressedThisFrame) {

            _warp = Math.Max(_warp - 1, 0);

        }

        if (keys.slashKey.wasPressedThisFrame) {

            _warp = 0;

        }

        if (keys.rKey.wasPressedThisFrame) {

            StartScenario();

            return;

        }

        if (keys.nKey.wasPressedThisFrame && !_node.HasValue) {

            double? periapsis = _vessel.Orbit.TimeAtTrueAnomaly(0.0, _time + 1.0);

            _node = new Maneuver(periapsis ?? _time + 600.0, 0.0, 0.0, 0.0);
            _nodeChanged = true;

        }

        if (!_node.HasValue) {

            return;

        }

        if (keys.deleteKey.wasPressedThisFrame || keys.backspaceKey.wasPressedThisFrame) {

            _node = null;
            _nodeChanged = true;

            return;

        }

        if (keys.gKey.wasPressedThisFrame) {

            _warp = WarpRates.Length - 1;

        }

        double scale = keys.shiftKey.isPressed ? 10.0 : keys.ctrlKey.isPressed ? 0.1 : 1.0;
        double rate = 10.0 * scale * dt;

        double prograde = Axis(keys.iKey, keys.kKey) * rate;
        double normal = Axis(keys.lKey, keys.jKey) * rate;
        double radial = Axis(keys.oKey, keys.uKey) * rate;

        double period = _vessel.Orbit.IsClosed ? _vessel.Orbit.Period : 3_600.0;
        double shift = Axis(keys.rightBracketKey, keys.leftBracketKey) * period * 0.1 * scale * dt;

        if (prograde == 0.0 && normal == 0.0 && radial == 0.0 && shift == 0.0) {

            return;

        }

        Maneuver node = _node.Value;

        _node = new Maneuver(Math.Max(_time + 1.0, node.Time + shift), node.Prograde + prograde, node.Normal + normal, node.Radial + radial);
        _nodeChanged = true;

    }

    private static double Axis(KeyControl positive, KeyControl negative) => (positive.isPressed ? 1.0 : 0.0) - (negative.isPressed ? 1.0 : 0.0);

    private void AdvanceClock(double seconds) {

        double next = _time + seconds;

        if (_node.HasValue && next >= _node.Value.Time) {

            next = _node.Value.Time;
            _warp = 0;

        }

        Patch current = _vessel.Current;

        if (current.End == PatchEnd.Impact && next >= current.EndTime) {

            next = current.EndTime;
            _warp = 0;

        }

        _time = next;
        _vessel.Advance(_time);

        if (_node.HasValue && _time >= _node.Value.Time) {

            _vessel.Execute(_node.Value);
            _node = null;
            _nodeChanged = true;

        }

    }

    private void Predict() {

        if (_predictedFrom == _vessel.Current && !_nodeChanged) {

            return;

        }

        Patch current = _vessel.Current;

        _actual = new List<Patch> { current };

        if (current.Continues) {

            (CelestialBody body, Orbit orbit) = Trajectory.Transition(current);
            _actual.AddRange(Trajectory.Predict(body, orbit, current.EndTime, PatchesAhead - 1));

        }

        _planned = _node.HasValue ? Trajectory.PredictWithManeuver(current.Body, current.Orbit, current.StartTime, _node.Value, PatchesAhead) : null;

        _predictedFrom = current;
        _nodeChanged = false;

    }

    private void FocusOn(int index, bool glide) {

        _focusIndex = index;

        switch (index) {

            case 0:

                _mapCamera.Focus(t => _vessel.PositionAt(t), 5_000.0, _time, glide);

                break;

            case 1:

                _mapCamera.Focus(_terra.PositionAt, _terra.Radius, _time, glide);

                break;

            default:

                _mapCamera.Focus(_selene.PositionAt, _selene.Radius, _time, glide);

                break;

        }

    }

    /// <summary>Burn at a periapsis that carries a Hohmann transfer to a close Selene flyby.</summary>
    private Maneuver? PlanSeleneTransfer() {

        Orbit orbit = _vessel.Orbit;
        double rp = orbit.SemiMajorAxis;
        double ra = _selene.Orbit.SemiMajorAxis;
        double deltaV = Math.Sqrt(_terra.Mu / rp) * (Math.Sqrt(2.0 * ra / (rp + ra)) - 1.0);

        Maneuver? best = null;
        double bestMiss = double.PositiveInfinity;

        for (int i = 0; i < 360; i++) {

            Maneuver candidate = new Maneuver(_time + 600.0 + orbit.Period * i / 360.0, deltaV, 0.0, 0.0);

            foreach (Patch patch in Trajectory.PredictWithManeuver(_terra, orbit, _time, candidate, 3)) {

                if (patch.Body != _selene) {

                    continue;

                }

                double miss = Math.Abs(patch.Orbit.PeriapsisRadius - (_selene.Radius + 400_000.0));

                if (miss < bestMiss) {

                    bestMiss = miss;
                    best = candidate;

                }

            }

        }

        return best;

    }

    private void DrawHud() {

        TimeSpan clock = TimeSpan.FromSeconds(_time);

        _hud.SetClock($"DAY {clock.Days + 1}", $"{clock:hh\\:mm\\:ss}", $"{WarpRates[_warp]:N0}×", _warp);

        CollectMarkers();
        _hud.SetMarkers(_markers, _camera);

    }

    // Order is priority: when labels overlap, the later one is hidden.
    private void CollectMarkers() {

        _markers.Clear();

        Patch current = _vessel.Current;
        (Vector3d r, Vector3d v) = _vessel.Orbit.StateAt(_time);
        bool impacted = current.End == PatchEnd.Impact && _time >= current.EndTime;
        string telemetry = impacted ? $"impacted {_vessel.Body.Name}" : $"{Distance(r.Length - _vessel.Body.Radius)}  ·  {v.Length:N0} m/s";

        _markers.Add(new Hud.Marker(MapSpace.ToScene(_vessel.PositionAt(_time)), _vessel.Name, "vessel", telemetry));

        List<Patch> shown = _planned ?? _actual;

        foreach (Patch patch in shown) {

            string within = Duration(patch.EndTime - _time);

            switch (patch.End) {

                case PatchEnd.Maneuver:

                    _markers.Add(new Hud.Marker(PatchPoint(patch, patch.EndTime), $"{_node.Value.DeltaV:N0} m/s", "node", $"burn in {within}"));

                    break;

                case PatchEnd.Encounter:

                    _markers.Add(new Hud.Marker(PatchPoint(patch, patch.EndTime), patch.NextBody.Name, "soi", $"encounter in {within}"));

                    break;

                case PatchEnd.Escape:

                    _markers.Add(new Hud.Marker(PatchPoint(patch, patch.EndTime), $"Leave {patch.Body.Name}", "soi", $"in {within}"));

                    break;

                case PatchEnd.Impact when !impacted:

                    _markers.Add(new Hud.Marker(PatchPoint(patch, patch.EndTime), "Impact", "impact", $"in {within}"));

                    break;

            }

        }

        foreach (Patch patch in shown) {

            AddApsides(patch);

        }

        foreach (BodyView view in _bodyViews) {

            CelestialBody body = view.Body;
            Vector3 top = MapSpace.ToScene(body.PositionAt(_time)) + _camera.transform.up * (float)(body.Radius / MapSpace.MetresPerUnit);

            _markers.Add(new Hud.Marker(top, body.Name.ToUpperInvariant(), "body"));

        }

    }

    private void AddApsides(Patch patch) {

        Orbit orbit = patch.Orbit;
        double radius = patch.Body.Radius;
        double from = Math.Max(_time, patch.StartTime);

        double? periapsis = orbit.TimeAtTrueAnomaly(0.0, from);

        if (periapsis.HasValue && periapsis.Value <= patch.EndTime) {

            _markers.Add(new Hud.Marker(PatchPoint(patch, periapsis.Value), "Pe", "apsis", Distance(orbit.PeriapsisRadius - radius)));

        }

        double? apoapsis = orbit.IsClosed ? orbit.TimeAtTrueAnomaly(Math.PI, from) : null;

        if (apoapsis.HasValue && apoapsis.Value <= patch.EndTime) {

            _markers.Add(new Hud.Marker(PatchPoint(patch, apoapsis.Value), "Ap", "apsis", Distance(orbit.ApoapsisRadius - radius)));

        }

    }

    private Vector3 PatchPoint(Patch patch, double time) => MapSpace.ToScene(OrbitLines.FrameOrigin(patch, _time) + patch.Orbit.StateAt(time).Position);

    private static string Distance(double metres) {

        double magnitude = Math.Abs(metres);

        if (magnitude < 10_000.0) {

            return $"{metres:N0} m";

        }

        return magnitude < 10_000_000.0 ? $"{metres / 1_000.0:N1} km" : $"{metres / 1_000.0:N0} km";

    }

    private static string Duration(double seconds) {

        if (double.IsInfinity(seconds)) {

            return "-";

        }

        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0.0, seconds));

        if (span.Days > 0) {

            return $"{span.Days}d {span:hh\\:mm}";

        }

        return span.Hours > 0 ? $"{span.Hours}:{span:mm\\:ss}" : $"{span:mm\\:ss}";

    }

}

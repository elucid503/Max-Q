using System;
using System.Collections.Generic;

using MaxQ.Game.Planet;
using MaxQ.Game.Planet.Ground;
using MaxQ.Game.Planet.Ground.Plants;
using MaxQ.Game.Planet.Ground.Regolith;
using MaxQ.Game.Planet.Sky;
using MaxQ.Game.Planet.Sky.Clouds;
using MaxQ.Game.Planet.Water;
using MaxQ.Game.Vessels;
using MaxQ.Game.Vessels.Craft;
using MaxQ.Game.Vessels.Flight;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;
using MaxQ.Sim.Orbits;
using MaxQ.Sim.Surface;
using MaxQ.Sim.Vessels;
using MaxQ.Sim.Vessels.Propulsion;

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Block-scoped: Unity's script importer cannot see classes in file-scoped namespaces, and a scene needs this one.
namespace MaxQ.Game.Map {

    /// <summary>The scene: owns the clock, flies the vessel with the chase camera behind it, or the free camera over Terra
    /// or Selene (V swaps cameras, Tab swaps bodies under the free camera), and draws it all.</summary>
    public sealed class MapView : MonoBehaviour {

        [SerializeField] private Material _groundMaterial;
        [SerializeField] private Material _waterMaterial;
        [SerializeField] private Material _rockMaterial;
        [SerializeField] private Material _grassMaterial;
        [SerializeField] private Material _treeMaterial;
        [SerializeField] private Material _regolithMaterial;
        [SerializeField] private Material _boulderMaterial;
        [SerializeField] private Shader _atmosphereTables;
        [SerializeField] private Shader _atmosphereSky;
        [SerializeField] private ComputeShader _exposure;
        [SerializeField] private Shader _cloudShader;
        [SerializeField] private ComputeShader _cloudNoise;
        [SerializeField] private ComputeShader _waves;
        [SerializeField] private Shader _waterCopy;
        [SerializeField] private Material _skyMaterial;
        [SerializeField] private Shader _cameraMotion;
        [SerializeField] private VesselArt _vesselArt;

        private static readonly int GroundNoiseId = Shader.PropertyToID("_GroundNoise");

        // Tab's destinations (degrees, metres, local solar hours): over the Alps, and over Hadley Rille at lunar morning.
        private static readonly (double Latitude, double Longitude, double Altitude, double Heading, double Pitch, double SolarHour) TerraSite =
            (46.58, 7.91, 20_000.0, 170.0, -20.0, 11.0);
        private static readonly (double Latitude, double Longitude, double Altitude, double Heading, double Pitch, double SolarHour) SeleneSite =
            (26.13, 3.63, 1_000.0, 190.0, -10.0, 6.75);

        // Where the vessel starts: 150 km over the eastern Mediterranean at mid-morning, heading north-east (degrees, hours).
        private static readonly (double Latitude, double Longitude, double Azimuth, double SolarHour) SpawnSite = (31.0, 24.0, 55.0, 10.5);
        private const double SpawnAltitude = 150_000.0;

        // Time warp steps; any control or a lit engine drops back to real time.
        private static readonly double[] WarpRates = { 1.0, 10.0, 100.0, 1_000.0, 10_000.0 };

        // Spent stages are drawn while within this of the camera (m); pixels within MotionKeep (m) are vessels.
        private const double DebrisReach = 100_000.0;
        private const double MotionKeep = 5_000.0;

        // Scene distance (km) past which the other body stands in, keeping depth-read distances within half precision.
        private const double StandInReach = 20_000.0;

        // Near Selene's ground the eye adapts a little by day and far at night; dusk spans these sun-elevation sines.
        private const double AirlessGround = 20_000.0;
        private const float DayStops = 2.0f;
        private const float NightStops = 12.0f;
        private const double DuskStart = 0.02;
        private const double DuskEnd = -0.05;

        // Chasing the vessel through Terra's shadow, the eye opens up as the sun leaves it, enough for what the plume and
        // the sunlit limb light; the starless dark beyond needs no more.
        private const float ShadowStops = 3.0f;

        // Sunlight above the air, in the renderer's units: sunlit ground of albedo a shows as a * 2.4 before the air dims
        // it. The air gives the sun its colour, so above it the light is white.
        private static readonly Color SunIlluminance = Color.white * (2.4f * Mathf.PI);

        // Sunlight travels along -X in the sim frame.
        private static readonly Vector3d Sunward = Vector3d.UnitX;

        private CelestialBody _terra;
        private CelestialBody _selene;
        private double _time;

        private Survey _terraSurvey;
        private Survey _seleneSurvey;
        private Texture2D _groundNoise;
        private GroundView _terraGround;
        private GroundView _seleneGround;
        private SeleneLight _seleneLight;
        private WaterView _water;
        private CloudView _clouds;
        private Atmosphere _atmosphere;
        private Sun _sun;
        private FreeCamera _freeCamera;
        private Camera _camera;
        private CameraMotion _motion;

        private Catalogue _catalogue;
        private CraftFile _craft;
        private Vessel _vessel;
        private VesselView _vesselView;
        private readonly List<VesselView> _debris = new List<VesselView>();
        private Pilot _pilot;
        private ChaseCamera _chase;
        private VesselLight _vesselLight;
        private bool _flying;
        private int _warp;

        private void Awake() {

            _terraSurvey = Survey.Terra(SurveyData.Directory("Terra") ?? "", SolarSystem.TerraRadius)
                ?? throw new InvalidOperationException("Terra's survey is not baked; run tools/terra.sh");
            _seleneSurvey = Survey.Selene(SurveyData.Directory("Selene") ?? "", SolarSystem.SeleneRadius)
                ?? throw new InvalidOperationException("Selene's survey is not baked; run tools/selene.sh");

            (_terra, _selene) = SolarSystem.Create(_terraSurvey.Terrain, _seleneSurvey.Terrain);

            _camera = Camera.main;
            _freeCamera = new FreeCamera(_camera);
            _motion = new CameraMotion(_cameraMotion);

            _clouds = new CloudView(_terra, _cloudShader, _cloudNoise, MapSpace.Direction(Sunward));
            _atmosphere = new Atmosphere(_terra, _clouds, _atmosphereTables, _atmosphereSky, _exposure, MapSpace.Direction(Sunward), SunIlluminance);
            _groundNoise = GroundNoise.Create();
            Shader.SetGlobalTexture(GroundNoiseId, _groundNoise);

            Vegetation vegetation = new Vegetation(_grassMaterial, _treeMaterial, (float)(_terra.Radius / MapSpace.MetresPerUnit));
            _terraGround = new GroundView(_terra, _groundMaterial, _waterMaterial, _rockMaterial, vegetation);
            _seleneGround = new GroundView(_selene, _regolithMaterial, null, _boulderMaterial, null);
            _seleneLight = new SeleneLight(_selene, _terra, SunIlluminance);
            _water = new WaterView(_terra, new SeaState(), _waves, _waterCopy);

            RenderSettings.skybox = _skyMaterial;

            _sun = new Sun(MapSpace.Direction(Sunward));

            _catalogue = new Catalogue(_vesselArt.Catalogue.text);
            _craft = CraftFile.Parse(_vesselArt.Craft.text);
            _pilot = new Pilot();
            _chase = new ChaseCamera(_camera);
            _vesselLight = new VesselLight(_sun, Sunward, _terra, _selene);

            Visit(_terra);
            Fly(SpawnSite.Latitude, SpawnSite.Longitude, SpawnSite.Azimuth, SpawnSite.SolarHour, 200.0, 12.0, 45.0);

        }

        private void OnDestroy() {

            _motion?.Dispose();
            _terraGround?.Dispose();
            _seleneGround?.Dispose();
            _water?.Dispose();
            Destroy(_groundNoise);
            _atmosphere?.Dispose();
            _clouds?.Dispose();
            _terraSurvey?.Dispose();
            _seleneSurvey?.Dispose();
            _vesselLight?.Dispose();
            DisposeVessels();

        }

        private void Update() {

            Keyboard keys = Keyboard.current;

            if (keys?.vKey.wasPressedThisFrame == true) {

                SwapCamera();

            }

            if (!_flying && keys?.tabKey.wasPressedThisFrame == true) {

                Visit(_freeCamera.Body == _terra ? _selene : _terra);

            }

            float dt = Time.unscaledDeltaTime;
            Controls controls = _pilot.Read(dt, _flying);

            if (ScriptedControls.HasValue) {

                controls = ScriptedControls.Value;

            }

            if (_flying && _pilot.StageRequested) {

                Stage();

            }

            _warp = Math.Clamp(_warp + _pilot.WarpStep, 0, WarpRates.Length - 1);

            if (!controls.IsIdle || !EnginesOff()) {

                _warp = 0;

            }

            _time += dt * WarpRates[_warp];
            _vessel.Advance(_time, controls);

            foreach (VesselView spent in _debris) {

                spent.Vessel.Advance(_time);

            }

            if (_flying) {

                _chase.Update(_vessel);

            } else {

                _freeCamera.Update(_time, dt);

            }

            CelestialBody body = _flying ? _vessel.Body : _freeCamera.Body;

            _motion.Update(body, _time, _flying ? MotionKeep : 0.0);

            GroundView beneath = body == _terra ? _terraGround : _seleneGround;
            Vector3 camera = _camera.transform.position;
            Vector3 sunward = MapSpace.Direction(Sunward);
            double reach = Math.Min(0.5 * _camera.farClipPlane, StandInReach);

            _terraGround.Draw(_time, _camera, sunward, body == _terra ? double.PositiveInfinity : reach, Atmosphere.AirThickness);
            _seleneGround.Draw(_time, _camera, sunward, body == _selene ? double.PositiveInfinity : reach, 0.0);

            float? airlessStops = null;

            if (body == _selene && beneath.CameraAltitude < AirlessGround) {

                double sunUp = Vector3d.Dot((MapSpace.Origin - _selene.PositionAt(_time)).Normalized, Sunward);
                double night = Math.Clamp((sunUp - DuskStart) / (DuskEnd - DuskStart), 0.0, 1.0);

                airlessStops = Mathf.Lerp(DayStops, NightStops, (float)(night * night * (3.0 - 2.0 * night)));

            } else if (_flying && _vesselLight.Sunlit < 1.0) {

                double shade = 1.0 - _vesselLight.Sunlit;

                airlessStops = ShadowStops * (float)(shade * shade * (3.0 - 2.0 * shade));

            }

            _clouds.Update(_time, camera);
            _atmosphere.Update(_time, camera, dt, airlessStops);
            _water.Update(_time, _camera);
            _freeCamera.WaveClearance = body == _terra ? 1.25 * _water.SeaHeight : 0.0;
            _seleneLight.Update(_time, Sunward, body == _selene ? beneath.CameraAltitude : double.PositiveInfinity);

            _vesselView.Draw(dt, true);

            foreach (VesselView spent in _debris) {

                spent.Draw(dt, Vector3d.Distance(spent.Vessel.RootPosition, MapSpace.Origin) < DebrisReach);

            }

            _vesselLight.Update(_vessel.RootPosition, _vessel.Body, _time);

            // Chasing, the nearest cascade holds the vessel; the rest step out to the horizon.
            double? nearest = _flying ? (_chase.Distance + VesselView.Reach) / MapSpace.MetresPerUnit : null;

            _sun.Fit(beneath.CameraAltitude / MapSpace.MetresPerUnit, body.Radius / MapSpace.MetresPerUnit, body.Terrain.Value.Highest / MapSpace.MetresPerUnit,
                nearest);

        }

        /// <summary>Starts a fresh vessel coasting 150 km over a place on Terra, travelling along <paramref name="azimuth"/>
        /// (degrees from north), with the clock at a local solar time there (hours) and the chase camera
        /// <paramref name="distance"/> metres off, <paramref name="heading"/> and <paramref name="pitch"/> degrees round.</summary>
        internal void Fly(double latitude, double longitude, double azimuth, double solarHour, double heading, double pitch, double distance) {

            _time = SolarTime(_terra, ((solarHour - 12.0) * 15.0 - longitude) * Math.PI / 180.0);
            Spawn(latitude, longitude, azimuth);
            _chase.Aim(heading, pitch, distance);
            _flying = true;
            _warp = 0;
            _water?.Jump();

        }

        /// <summary>Controls a capture holds instead of the keyboard's; null hands back to the pilot.</summary>
        internal Controls? ScriptedControls { get; set; }

        internal Vessel Vessel => _vessel;

        /// <summary>Fires the next decoupler; the spent stage flies on as debris.</summary>
        internal void Stage() {

            Vessel spent = _vessel.Stage();

            if (spent == null) {

                return;

            }

            _debris.Add(new VesselView(spent, _catalogue, _vesselArt));
            _vesselView.Dispose();
            _vesselView = new VesselView(_vessel, _catalogue, _vesselArt);

        }

        // The craft, stacked fresh and coasting on a circular orbit through the point above the place, nose forward and
        // its X axis to the ground.
        private void Spawn(double latitude, double longitude, double azimuth) {

            double lat = latitude * Math.PI / 180.0;
            double lon = longitude * Math.PI / 180.0;
            double az = azimuth * Math.PI / 180.0;
            Vector3d up = new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
            Vector3d east = Vector3d.Cross(Vector3d.UnitZ, up).Normalized;
            Vector3d north = Vector3d.Cross(up, east);
            double radius = _terra.Radius + SpawnAltitude;

            Vector3d position = _terra.FromBodyFixed(up * radius, _time);
            Vector3d velocity = _terra.FromBodyFixed((north * Math.Cos(az) + east * Math.Sin(az)) * Math.Sqrt(_terra.Mu / radius), _time);
            Vector3d nose = velocity.Normalized;
            Vector3d belly = -position.Normalized;

            DisposeVessels();

            _vessel = new Vessel(_craft.name, _craft.Build(_catalogue), _terra, Orbit.FromStateVectors(position, velocity, _terra.Mu, _time), _time,
                QuaternionD.FromBasis(belly, Vector3d.Cross(nose, belly), nose));
            _vesselView = new VesselView(_vessel, _catalogue, _vesselArt);
            _pilot.Release();

        }

        private void DisposeVessels() {

            _vesselView?.Dispose();
            _vesselView = null;

            foreach (VesselView spent in _debris) {

                spent.Dispose();

            }

            _debris.Clear();

        }

        private bool EnginesOff() {

            foreach (Engine engine in _vessel.Engines) {

                if (engine.Phase != EnginePhase.Off) {

                    return false;

                }

            }

            return true;

        }

        // The free camera takes over where the vessel is; the chase camera picks the vessel back up.
        private void SwapCamera() {

            _flying = !_flying;

            if (_flying) {

                return;

            }

            CelestialBody body = _vessel.Body;
            Vector3d fixedPosition = body.ToBodyFixed(_vessel.State.Position, _time);
            double r = fixedPosition.Length;

            _freeCamera.Place(body, Math.Asin(fixedPosition.Z / r) * 180.0 / Math.PI, Math.Atan2(fixedPosition.Y, fixedPosition.X) * 180.0 / Math.PI,
                r - body.Radius, 0.0, -20.0);

        }

        /// <summary>Flies the free camera to a place on Terra and sets the clock to a local solar time there (hours).</summary>
        internal void Look(double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) =>
            Look(_terra, latitude, longitude, altitude, heading, pitch, solarHour);

        /// <summary>The same over Selene.</summary>
        internal void LookFromSelene(double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) =>
            Look(_selene, latitude, longitude, altitude, heading, pitch, solarHour);

        private void Visit(CelestialBody body) {

            (double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) = body == _terra ? TerraSite : SeleneSite;

            Look(body, latitude, longitude, altitude, heading, pitch, solarHour);

        }

        // Places the free camera and sets the clock to that local solar time within the body's first solar day; the clock
        // may run back, so the vessel starts afresh at its spawn.
        private void Look(CelestialBody body, double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) {

            _time = SolarTime(body, ((solarHour - 12.0) * 15.0 - longitude) * Math.PI / 180.0);
            _freeCamera.Place(body, latitude, longitude, altitude, heading, pitch);
            _flying = false;
            _warp = 0;
            _water?.Jump();

            if (_vessel != null) {

                Spawn(SpawnSite.Latitude, SpawnSite.Longitude, SpawnSite.Azimuth);

            }

        }

        // When the sun stands over a body-fixed longitude (radians, negated): coarse scan, then bisection.
        private static double SolarTime(CelestialBody body, double negatedLongitude) {

            const int steps = 720;

            double day = body.RotationPeriodSeconds;
            double best = 0.0;
            double closest = double.MaxValue;

            for (int i = 0; i < steps; i++) {

                double t = day * i / steps;
                double gap = Math.Abs(Offset(body, t, negatedLongitude));

                if (gap < closest) {

                    closest = gap;
                    best = t;

                }

            }

            double low = best - day / steps;
            double high = best + day / steps;

            for (int i = 0; i < 40; i++) {

                double middle = 0.5 * (low + high);

                if (Offset(body, middle, negatedLongitude) > 0.0) {

                    low = middle;

                } else {

                    high = middle;

                }

            }

            double time = 0.5 * (low + high);

            return (time % day + day) % day;

        }

        // Radians the sun's body-fixed longitude stands east of the target, wrapped.
        private static double Offset(CelestialBody body, double time, double negatedLongitude) {

            Vector3d sun = body.ToBodyFixed(Sunward, time);
            double gap = Math.Atan2(sun.Y, sun.X) + negatedLongitude;

            return Math.IEEERemainder(gap, 2.0 * Math.PI);

        }

        internal GroundView Ground => _terraGround;

        internal Atmosphere Atmosphere => _atmosphere;

        internal CloudView Clouds => _clouds;

        internal WaterView Water => _water;

        internal Sun Sun => _sun;

    }

}

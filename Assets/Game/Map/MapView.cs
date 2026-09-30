using System;

using MaxQ.Game.Planet;
using MaxQ.Game.Planet.Ground;
using MaxQ.Game.Planet.Ground.Plants;
using MaxQ.Game.Planet.Ground.Regolith;
using MaxQ.Game.Planet.Sky;
using MaxQ.Game.Planet.Sky.Clouds;
using MaxQ.Game.Planet.Water;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;
using MaxQ.Sim.Surface;

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Block-scoped: Unity's script importer cannot see classes in file-scoped namespaces, and a scene needs this one.
namespace MaxQ.Game.Map {

    /// <summary>The scene: owns the clock, flies the free camera over Terra or Selene (Tab switches) and draws both.</summary>
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

        private static readonly int GroundNoiseId = Shader.PropertyToID("_GroundNoise");

        // Tab's destinations (degrees, metres, local solar hours): over the Alps, and over Hadley Rille at lunar morning.
        private static readonly (double Latitude, double Longitude, double Altitude, double Heading, double Pitch, double SolarHour) TerraSite =
            (46.58, 7.91, 20_000.0, 170.0, -20.0, 11.0);
        private static readonly (double Latitude, double Longitude, double Altitude, double Heading, double Pitch, double SolarHour) SeleneSite =
            (26.13, 3.63, 1_000.0, 190.0, -10.0, 6.75);

        // Scene distance (km) past which the other body stands in, keeping depth-read distances within half precision.
        private const double StandInReach = 20_000.0;

        // Near Selene's ground the eye adapts a little by day and far at night; dusk spans these sun-elevation sines.
        private const double AirlessGround = 20_000.0;
        private const float DayStops = 2.0f;
        private const float NightStops = 12.0f;
        private const double DuskStart = 0.02;
        private const double DuskEnd = -0.05;

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
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.025f, 0.028f, 0.035f);

            _sun = new Sun(MapSpace.Direction(Sunward));

            Visit(_terra);

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

        }

        private void Update() {

            if (Keyboard.current?.tabKey.wasPressedThisFrame == true) {

                Visit(_freeCamera.Body == _terra ? _selene : _terra);

            }

            float dt = Time.unscaledDeltaTime;

            _time += dt;
            _freeCamera.Update(_time, dt);

            CelestialBody body = _freeCamera.Body;

            _motion.Update(body, _time);

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

            }

            _clouds.Update(_time, camera);
            _atmosphere.Update(_time, camera, dt, airlessStops);
            _water.Update(_time, _camera);
            _freeCamera.WaveClearance = body == _terra ? 1.25 * _water.SeaHeight : 0.0;
            _seleneLight.Update(_time, Sunward, body == _selene ? beneath.CameraAltitude : double.PositiveInfinity);
            _sun.Fit(beneath.CameraAltitude / MapSpace.MetresPerUnit, body.Radius / MapSpace.MetresPerUnit, body.Terrain.Value.Highest / MapSpace.MetresPerUnit);

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

        // Places the camera and sets the clock to that local solar time within the body's first solar day.
        private void Look(CelestialBody body, double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) {

            _time = SolarTime(body, ((solarHour - 12.0) * 15.0 - longitude) * Math.PI / 180.0);
            _freeCamera.Place(body, latitude, longitude, altitude, heading, pitch);
            _water?.Jump();

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

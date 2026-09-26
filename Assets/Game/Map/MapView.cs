using System;
using System.IO;

using MaxQ.Game.Map.Rendering;
using MaxQ.Game.Planet;
using MaxQ.Game.Planet.Ground;
using MaxQ.Game.Planet.Sky;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using UnityEngine;
using UnityEngine.Rendering;

// Block-scoped: Unity's script importer cannot see classes in file-scoped namespaces, and a scene needs this one.
namespace MaxQ.Game.Map {

    /// <summary>The scene: owns the clock, flies the free camera over Terra and draws the ground, sky and Selene in order.</summary>
    public sealed class MapView : MonoBehaviour {

        [SerializeField] private Texture2D[] _seleneFaces;
        [SerializeField] private Material _surfaceMaterial;
        [SerializeField] private Material _groundMaterial;
        [SerializeField] private Material _waterMaterial;
        [SerializeField] private Material _rockMaterial;
        [SerializeField] private Material _grassMaterial;
        [SerializeField] private Material _treeMaterial;
        [SerializeField] private ComputeShader _vegetation;
        [SerializeField] private Shader _atmosphereTables;
        [SerializeField] private Shader _atmosphereSky;
        [SerializeField] private ComputeShader _exposure;
        [SerializeField] private Material _skyMaterial;

        private CelestialBody _terra;
        private double _time;

        private Survey _survey;
        private GroundView _ground;
        private Atmosphere _atmosphere;
        private Sun _sun;
        private BodyView _seleneView;
        private FreeCamera _freeCamera;
        private Camera _camera;

        private void Awake() {

            string data = TerraData.Directory;
            _survey = data == null ? null : Survey.Open(data, SolarSystem.TerraRadius);

            if (_survey == null) {

                throw new InvalidOperationException("Terra's survey is not baked; run tools/terra.sh");

            }

            (_terra, CelestialBody selene) = SolarSystem.Create(_survey.Terrain);

            _camera = Camera.main;
            _freeCamera = new FreeCamera(_camera);

            _atmosphere = new Atmosphere(_terra, _atmosphereTables, _atmosphereSky, _exposure, MapSpace.Direction(Vector3d.UnitX), SunIlluminance);
            Vegetation vegetation = new Vegetation(_grassMaterial, _treeMaterial, _vegetation, _groundMaterial.GetTexture("_GroundAlbedo"), (float)(_terra.Radius / MapSpace.MetresPerUnit));
            _ground = new GroundView(_terra, new ColourTiles(Path.Combine(data, "colour.tiles")), _groundMaterial, _waterMaterial, _rockMaterial, vegetation);
            _seleneView = new BodyView(selene, _seleneFaces, _surfaceMaterial, 0.0f);

            RenderSettings.skybox = _skyMaterial;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.025f, 0.028f, 0.035f);

            // Sunlight travels along -X in the sim frame.
            _sun = new Sun(MapSpace.Direction(Vector3d.UnitX));

            // Play starts high over the Alps in the late morning.
            Look(46.58, 7.91, 20_000.0, 170.0, -20.0, 11.0);

        }

        // Sunlight above the air, in the renderer's units: sunlit ground of albedo a shows as a * 2.4 before the air dims
        // it. The air gives the sun its colour, so above it the light is white.
        private static readonly Color SunIlluminance = Color.white * (2.4f * Mathf.PI);

        private void OnDestroy() {

            _ground?.Dispose();
            _atmosphere?.Dispose();
            _survey?.Dispose();

        }

        private void Update() {

            float dt = Time.unscaledDeltaTime;

            _time += dt;
            _freeCamera.Update(_time, dt);

            _atmosphere.Update(_time, _camera.transform.position, dt);
            _ground.Draw(_time, _camera, MapSpace.Direction(Vector3d.UnitX));
            _sun.Fit(_ground.CameraAltitude / MapSpace.MetresPerUnit, _terra.Radius / MapSpace.MetresPerUnit);
            _seleneView.Draw(_time, _camera);

        }

        /// <summary>Flies the free camera to a place and sets the clock to a local solar time there (hours), within the first day.</summary>
        internal void Look(double latitude, double longitude, double altitude, double heading, double pitch, double solarHour) {

            double day = _terra.RotationPeriodSeconds;
            double time = ((solarHour - 12.0) * 15.0 - longitude) / 360.0 * day;

            _time = (time % day + day) % day;
            _freeCamera.Place(_terra, latitude, longitude, altitude, heading, pitch);

        }

        internal GroundView Ground => _ground;

        internal Atmosphere Atmosphere => _atmosphere;

        internal Sun Sun => _sun;

    }

}

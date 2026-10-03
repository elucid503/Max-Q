using System;

using MaxQ.Game.Map;
using MaxQ.Game.Vessels.Hull;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Rendering;

using Terrain = MaxQ.Sim.Surface.Terrain;

namespace MaxQ.Game.Site;

/// <summary>The pad at Cape Canaveral, on LC-39A's latitude: Terra's ground levelled round it, a concrete hardstand, a flame trench
/// between two concrete walls running north and south with a steel deflector in it, and a steel launch table over the
/// trench with four hold-down clamps round a hole the engines fire through. Drawn in metres from the levelled ground at
/// its centre, X east, Y up, Z north.</summary>
public sealed class LaunchPad : IDisposable {

    // LC-39A's latitude; its longitude would put the pad in the survey's sea, which is coarser than Merritt Island, so it
    // stands on the nearest ground the survey holds dry all round, 5 km west at this scale.
    private const double Latitude = 28.6083;
    private const double Longitude = -80.86;

    /// <summary>The levelled ground's height over Terra's reference radius, m, and the radii it lies flat within and blends
    /// back over.</summary>
    public const double Grade = 2.0;
    private const double FlatRadius = 120.0;
    private const double BlendRadius = 250.0;

    private const int Concrete = 0;
    private const int Steel = 1;

    // The hardstand: square with cut corners, standing a little proud of the ground and buried deeper.
    private const float SlabSize = 90.0f;
    private const float SlabProud = 0.3f;
    private const float SlabDepth = 1.5f;
    private const float SlabBevel = 12.0f;

    // The trench walls: thickness, height over the slab and length; the trench between them.
    private const float WallThickness = 4.0f;
    private const float WallHeight = 7.0f;
    private const float WallLength = 34.0f;
    private const float TrenchWidth = 8.0f;

    // The launch table spanning the walls, and the square hole in it, wide enough for the engines and the gimbals' swing.
    private const float TableLength = 12.0f;
    private const float TableThickness = 1.5f;
    private const float HoleWidth = 4.6f;

    // The deflector: two steel plates meeting in a ridge under the hole, splitting the exhaust north and south.
    private const float DeflectorHeight = 4.5f;
    private const float DeflectorSlope = 50.0f;
    private const float PlateThickness = 0.4f;

    // Hold-down clamps round the hole: a pedestal the stage rests on and an arm over its rim.
    private const int Clamps = 4;
    private const float ClampRadius = 2.45f;
    private const float ClampHeight = 1.0f;

    // Past this from the camera (scene units, km) the pad is not drawn.
    private const float DrawReach = 200.0f;

    private readonly CelestialBody _body;
    private readonly GameObject _root;
    private readonly Mesh _mesh;

    /// <summary>The pad's unit body-fixed direction, and the ground's east and north there.</summary>
    public static readonly Vector3d Up;
    private static readonly Vector3d East;
    private static readonly Vector3d North;

    static LaunchPad() {

        double lat = Latitude * Math.PI / 180.0;
        double lon = Longitude * Math.PI / 180.0;

        Up = new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        East = Vector3d.Cross(Vector3d.UnitZ, Up).Normalized;
        North = Vector3d.Cross(Up, East);

    }

    /// <summary>Height over the levelled ground the stage stands at: the clamps' tops on the table.</summary>
    public static double Rest => SlabProud + WallHeight + TableThickness + ClampHeight;

    /// <summary>The longitude, radians east, for the sun's hours there.</summary>
    public static double LongitudeRadians => Longitude * Math.PI / 180.0;

    /// <summary>Terra's ground with the pad's site levelled on it.</summary>
    public static Terrain Level(Terrain terrain) => terrain.WithSite(Up, Grade, FlatRadius, BlendRadius);

    /// <summary>Where a stack stands on the clamps, body-fixed: its station zero, <paramref name="drop"/> metres below the
    /// station that rests on them, and its attitude, nose up with body Y east, so pitching nose-down tips it east.</summary>
    public static (Vector3d Datum, QuaternionD Attitude) Stand(CelestialBody body, double drop) {

        Vector3d datum = Up * (body.Radius + Grade + Rest - drop);

        return (datum, QuaternionD.FromBasis(-North, East, Up));

    }

    public LaunchPad(CelestialBody body, Material concrete, Material steel) {

        _body = body;
        _mesh = Build();
        _root = new GameObject("Launch Pad");
        _root.transform.localScale = Vector3.one * (float)(1.0 / MapSpace.MetresPerUnit);
        _root.AddComponent<MeshFilter>().sharedMesh = _mesh;

        MeshRenderer renderer = _root.AddComponent<MeshRenderer>();

        renderer.sharedMaterials = new[] { concrete, steel };
        renderer.shadowCastingMode = ShadowCastingMode.On;

    }

    /// <summary>Places the pad where the turning ground has it, relative to the scene's origin.</summary>
    public void Draw(double time) {

        Vector3 position = MapSpace.ToScene(_body.PositionAt(time) + _body.FromBodyFixed(Up * (_body.Radius + Grade), time));

        _root.SetActive(position.magnitude < DrawReach);

        if (!_root.activeSelf) {

            return;

        }

        Quaternion rotation = Quaternion.LookRotation(MapSpace.Direction(_body.FromBodyFixed(North, time)), MapSpace.Direction(_body.FromBodyFixed(Up, time)));

        _root.transform.SetPositionAndRotation(position, rotation);

    }

    public void Dispose() {

        UnityEngine.Object.Destroy(_root);
        UnityEngine.Object.Destroy(_mesh);

    }

    private static Mesh Build() {

        MeshBuilder mesh = new MeshBuilder(2);
        Quaternion level = Quaternion.identity;

        mesh.Box(new Vector3(0.0f, SlabProud - 0.5f * SlabDepth, 0.0f), new Vector3(SlabSize, SlabDepth, SlabSize), level, SlabBevel, Concrete);

        // The walls either side of the trench.
        float wallCentre = 0.5f * (TrenchWidth + WallThickness);

        foreach (float wall in new[] { -1.0f, 1.0f }) {

            mesh.Box(new Vector3(wall * wallCentre, SlabProud + 0.5f * WallHeight, 0.0f), new Vector3(WallThickness, WallHeight, WallLength), level, 0.1f, Concrete);

        }

        // The table: four beams round the hole, spanning both walls.
        float tableTop = SlabProud + WallHeight + TableThickness;
        float tableWidth = TrenchWidth + 2.0f * WallThickness;
        float tableY = tableTop - 0.5f * TableThickness;
        float beam = 0.5f * (tableWidth - HoleWidth);
        float end = 0.5f * (TableLength - HoleWidth);

        mesh.Box(new Vector3(-0.5f * (HoleWidth + beam), tableY, 0.0f), new Vector3(beam, TableThickness, TableLength), level, 0.05f, Steel);
        mesh.Box(new Vector3(0.5f * (HoleWidth + beam), tableY, 0.0f), new Vector3(beam, TableThickness, TableLength), level, 0.05f, Steel);
        mesh.Box(new Vector3(0.0f, tableY, -0.5f * (HoleWidth + end)), new Vector3(HoleWidth, TableThickness, end), level, 0.05f, Steel);
        mesh.Box(new Vector3(0.0f, tableY, 0.5f * (HoleWidth + end)), new Vector3(HoleWidth, TableThickness, end), level, 0.05f, Steel);

        // The deflector's plates, leaning away north and south from a ridge running east and west.
        float slope = DeflectorSlope * Mathf.Deg2Rad;
        float plate = DeflectorHeight / Mathf.Sin(slope);

        foreach (float half in new[] { -1.0f, 1.0f }) {

            Quaternion lean = Quaternion.AngleAxis(-half * (90.0f - DeflectorSlope), Vector3.right);
            Vector3 middle = new Vector3(0.0f, SlabProud + 0.5f * DeflectorHeight, half * 0.5f * plate * Mathf.Cos(slope));

            mesh.Box(middle, new Vector3(TrenchWidth, plate, PlateThickness), lean, 0.0f, Steel);

        }

        // The clamps, on the table's edge round the hole, each pedestal under the stage's rim and an arm over it.
        for (int i = 0; i < Clamps; i++) {

            float angle = 2.0f * Mathf.PI * i / Clamps;
            Vector3 outward = new Vector3(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle));
            Quaternion facing = Quaternion.LookRotation(outward, Vector3.up);

            mesh.Box(outward * ClampRadius + Vector3.up * (tableTop + 0.5f * ClampHeight), new Vector3(0.7f, ClampHeight, 0.9f), facing, 0.1f, Steel);
            mesh.Box(outward * (ClampRadius - 0.35f) + Vector3.up * (tableTop + ClampHeight + 0.15f), new Vector3(0.35f, 0.3f, 0.6f), facing, 0.05f, Steel);

        }

        return mesh.Build("Launch Pad");

    }

}

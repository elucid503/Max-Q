using System;
using System.Collections.Generic;

using MaxQ.Game.Map;
using MaxQ.Game.Vessels.Craft;
using MaxQ.Game.Vessels.Hull;
using MaxQ.Game.Vessels.Plume;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels;
using MaxQ.Sim.Vessels.Parts;
using MaxQ.Sim.Vessels.Propulsion;

using UnityEngine;

namespace MaxQ.Game.Vessels;

/// <summary>Draws one vessel: a root at its stack datum in kilometre scene units holding each part in metres, every engine
/// swinging on its gimbal, their plumes, and a puff at every RCS nozzle that fires.</summary>
public sealed class VesselView : IDisposable {

    private const float MetresToScene = (float)(1.0 / MapSpace.MetresPerUnit);

    // What a stack's reach adds beyond its ends: nozzles swung out, the capsule's nose.
    private const double ReachMargin = 2.0;

    private readonly GameObject _root;
    private readonly List<Mesh> _meshes = new List<Mesh>();
    private readonly List<(Engine Engine, int Nozzle, Transform Pivot, NozzleGlow Glow, Light Light, float Intensity)> _engines =
        new List<(Engine, int, Transform, NozzleGlow, Light, float)>();
    private readonly List<(Engine Engine, ExhaustRenderer Exhaust)> _exhausts = new List<(Engine, ExhaustRenderer)>();
    private readonly List<(Thruster Thruster, PlumeRenderer Puff)> _puffs = new List<(Thruster, PlumeRenderer)>();

    public Vessel Vessel { get; }

    /// <summary>How far the stack reaches from its centre of mass, metres; cameras and shadows keep it in view.</summary>
    public double Reach {

        get {

            double centre = Vessel.MassProperties.CentreOfMass;

            return Math.Max(centre - Vessel.Bottom, Vessel.Top - centre) + ReachMargin;

        }

    }

    public VesselView(Vessel vessel, Catalogue catalogue, CraftFile craft, VesselArt art) {

        Vessel = vessel;
        _root = new GameObject(vessel.Name);
        _root.transform.localScale = Vector3.one * MetresToScene;

        Part below = null;

        foreach (Part part in vessel.Parts) {

            Transform holder = new GameObject(part.Name).transform;

            holder.SetParent(_root.transform, false);
            holder.localPosition = new Vector3(0.0f, (float)part.Station, 0.0f);

            switch (part) {

                case Engine engine:

                    Transform[] pivots = new Transform[engine.Count];

                    for (int i = 0; i < engine.Count; i++) {

                        pivots[i] = AddEngine(engine, i, catalogue.Engine(engine.Name), holder, art);

                    }

                    AddExhaust(engine, catalogue.Engine(engine.Name), engine.Count == 1 ? pivots[0] : holder, art);

                    break;

                case Capsule capsule:

                    AddModel(catalogue.Capsule(capsule.Name).fit, holder, Vector3.zero, art);

                    break;

                case Tank tank:

                    Engine hung = below as Engine;

                    AddMesh(HullMesh.Tank(tank, hung == null ? null : catalogue.Engine(hung.Name), hung), holder, art);

                    break;

                case Skirt skirt:

                    AddMesh(HullMesh.Skirt(skirt, Finish(craft.Line(skirt).finish)), holder, art);

                    break;

                case Decoupler ring:

                    AddMesh(HullMesh.Decoupler(ring, craft.Line(ring).open), holder, art);

                    break;

                case RcsBlock block:

                    AddMesh(HullMesh.Pods(block, catalogue.Pod(block.Name)), holder, art);

                    break;

            }

            PlumeLayer[] jet = part switch {

                RcsBlock block => catalogue.Pod(block.Name).plume,
                Capsule capsule => catalogue.Capsule(capsule.Name).plume,
                _ => null,

            };

            foreach (Thruster thruster in part.Thrusters) {

                PlumeRenderer puff = new PlumeRenderer(art.Plume, holder, jet);

                puff.Place(ToScene(thruster.LocalPosition), Quaternion.FromToRotation(Vector3.up, ToScene(thruster.Direction)));
                _puffs.Add((thruster, puff));

            }

            below = part;

        }

        foreach (Renderer renderer in _root.GetComponentsInChildren<Renderer>()) {

            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;

        }

    }

    /// <summary>Moves the vessel to where the sim has it at its own time, relative to the scene's origin.</summary>
    public void Draw(float deltaSeconds, bool shown) {

        _root.SetActive(shown && !Vessel.HasImpacted);

        if (!_root.activeSelf) {

            return;

        }

        QuaternionD q = Vessel.State.Attitude;

        _root.transform.SetPositionAndRotation(MapSpace.ToScene(Vessel.Body.PositionAt(Vessel.Time) + Vessel.Datum), new Quaternion((float)-q.X, (float)-q.Z, (float)-q.Y, (float)q.W));

        foreach ((Engine engine, int nozzle, Transform pivot, NozzleGlow glow, Light light, float intensity) in _engines) {

            pivot.localRotation = Quaternion.FromToRotation(Vector3.up, ToScene(engine.Direction(nozzle)));

            if (glow != null) {

                glow.Temperature = (float)engine.NozzleTemperature;

            }

            if (light != null) {

                light.enabled = engine.Chamber > 0.01;
                light.intensity = intensity * (float)engine.Chamber;

            }

        }

        foreach ((Engine engine, ExhaustRenderer exhaust) in _exhausts) {

            exhaust.Strength = (float)engine.Chamber;
            exhaust.Pressure = (float)engine.AmbientPressure;

            // A cluster's one exhaust leaves along the engines' mean thrust; a single engine's swings on its own gimbal.
            if (engine.Count > 1) {

                Vector3d thrust = Vector3d.Zero;

                for (int i = 0; i < engine.Count; i++) {

                    thrust += engine.Direction(i);

                }

                exhaust.Place(Vector3.zero, Quaternion.FromToRotation(Vector3.down, -ToScene(thrust.Normalized)) * Downstream);

            }

        }

        foreach ((Thruster thruster, PlumeRenderer puff) in _puffs) {

            puff.Pulse(thruster.Firing, deltaSeconds);

        }

    }

    public void Dispose() {

        UnityEngine.Object.Destroy(_root);

        foreach (Mesh mesh in _meshes) {

            UnityEngine.Object.Destroy(mesh);

        }

    }

    /// <summary>A body-frame vector in the stack's scene axes.</summary>
    private static Vector3 ToScene(Vector3d v) => new Vector3((float)v.X, (float)v.Z, (float)v.Y);

    private static int Finish(string name) => name switch {

        null or "paint" => HullMesh.Paint,
        "metal" => HullMesh.Metal,
        "dark" => HullMesh.Dark,
        _ => throw new InvalidOperationException($"No hull finish named '{name}'."),

    };

    // Turns an exhaust's +Y downstream, out of the exit; it takes the holder's Z to -Z.
    private static readonly Quaternion Downstream = Quaternion.Euler(180.0f, 0.0f, 0.0f);

    // One exhaust for the engine or the whole cluster: on a single engine's gimbal at its exit, or at a cluster's exit plane.
    private void AddExhaust(Engine engine, EngineEntry entry, Transform parent, VesselArt art) {

        Vector2[] nozzles = new Vector2[engine.Count];

        for (int i = 0; i < nozzles.Length && engine.Count > 1; i++) {

            Vector3 exit = Downstream * ToScene(engine.Nozzles[i]);

            nozzles[i] = new Vector2(exit.x, exit.z);

        }

        ExhaustRenderer exhaust = new ExhaustRenderer(art.Exhaust, parent, entry, nozzles, Reach);

        exhaust.Place(engine.Count > 1 ? Vector3.zero : new Vector3(0.0f, -(float)engine.Length, 0.0f), Downstream);
        _exhausts.Add((engine, exhaust));

    }

    private Transform AddEngine(Engine engine, int nozzle, EngineEntry entry, Transform holder, VesselArt art) {

        float length = (float)engine.Length;
        Transform pivot = new GameObject("Gimbal").transform;

        pivot.SetParent(holder, false);
        pivot.localPosition = ToScene(engine.Nozzles[nozzle]) + new Vector3(0.0f, length, 0.0f);

        // Off the axis, each engine is turned so whatever stands out from its side points round the cluster, not out of it.
        Vector3d offset = engine.Nozzles[nozzle];
        double turn = offset.Length > 1e-6 ? Math.Atan2(offset.Y, offset.X) + 0.5 * Math.PI : 0.0;
        GameObject model = AddModel(entry.fit, pivot, new Vector3(0.0f, -length, 0.0f), art, turn);

        // JsonUtility fills absent entries with zeros rather than leaving them null.
        NozzleGlow glow = entry.extension is { heatCapacity: > 0.0 } ? new NozzleGlow(art.NozzleGlow, pivot, model, engine.Length, entry.extension) : null;

        if (glow != null) {

            _meshes.Add(glow.Mesh);

        }

        Light light = null;
        float intensity = 0.0f;

        // The exhaust lights its own bell from inside, its walls about an exit radius off. URP's inverse square runs in
        // scene units (km): a strength given at a metre is a millionth of that at a kilometre.
        if (entry.light is { intensity: > 0.0 } inside) {

            light = new GameObject("Exhaust Light").AddComponent<Light>();
            light.transform.SetParent(pivot, false);
            light.transform.localPosition = new Vector3(0.0f, (float)(inside.height - engine.Length), 0.0f);
            light.type = LightType.Point;
            light.range = (float)(Math.Max(inside.range, VesselLight.NearbyRange) / MapSpace.MetresPerUnit);
            // A light's colour is sRGB; the catalogue's is linear.
            light.color = inside.colour is { Length: 3 } ? new Color((float)inside.colour[0], (float)inside.colour[1], (float)inside.colour[2]).gamma : Color.white;
            light.shadows = LightShadows.None;
            light.enabled = false;
            intensity = VesselLight.Nearby(engine.ExitRadius) * (float)(inside.intensity / (MapSpace.MetresPerUnit * MapSpace.MetresPerUnit));

        }

        _engines.Add((engine, nozzle, pivot, glow, light, intensity));

        return pivot;

    }

    private static GameObject AddModel(ModelFit fit, Transform parent, Vector3 origin, VesselArt art, double turn = 0.0) {

        // The fit goes on a holder: an imported model's root carries its own turn and offset, which must stay.
        Transform holder = new GameObject("Fit").transform;

        holder.SetParent(parent, false);
        holder.localPosition = origin + HullMesh.Scene(fit.offset);
        holder.localRotation = Quaternion.AngleAxis((float)(-(fit.turn + turn) * Mathf.Rad2Deg), Vector3.up);
        holder.localScale = Vector3.one * (float)fit.scale;

        GameObject model = UnityEngine.Object.Instantiate(art.Model(fit.model), holder, false);

        foreach (Transform node in model.GetComponentsInChildren<Transform>(true)) {

            if (Array.IndexOf(fit.hide ?? Array.Empty<string>(), node.name) >= 0) {

                node.gameObject.SetActive(false);

            }

        }

        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true)) {

            Material[] materials = renderer.sharedMaterials;

            for (int i = 0; i < materials.Length; i++) {

                foreach (FinishEntry finish in fit.finishes ?? Array.Empty<FinishEntry>()) {

                    if (materials[i] != null && materials[i].name == finish.material) {

                        materials[i] = art.Finish(finish.finish);

                    }

                }

            }

            renderer.sharedMaterials = materials;

        }

        return model;

    }

    private void AddMesh(Mesh mesh, Transform holder, VesselArt art) {

        _meshes.Add(mesh);
        holder.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        holder.gameObject.AddComponent<MeshRenderer>().sharedMaterials = art.Finishes;

    }

}

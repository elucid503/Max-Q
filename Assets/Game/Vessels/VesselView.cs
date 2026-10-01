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

/// <summary>Draws one vessel: a root at its stack datum in kilometre scene units holding each part in metres, the engine
/// swinging on its gimbal, its plume, and a puff at every RCS nozzle that fires.</summary>
public sealed class VesselView : IDisposable {

    /// <summary>How far a stack reaches from its centre of mass, metres; cameras and shadows keep it in view.</summary>
    public const double Reach = 12.0;

    private const float MetresToScene = (float)(1.0 / MapSpace.MetresPerUnit);

    private readonly GameObject _root;
    private readonly List<Mesh> _meshes = new List<Mesh>();
    private readonly List<(Engine Engine, Transform Pivot, ExhaustRenderer Exhaust, Light Light, float Intensity)> _engines =
        new List<(Engine, Transform, ExhaustRenderer, Light, float)>();
    private readonly List<(Thruster Thruster, PlumeRenderer Puff)> _puffs = new List<(Thruster, PlumeRenderer)>();

    public Vessel Vessel { get; }

    public VesselView(Vessel vessel, Catalogue catalogue, VesselArt art) {

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

                    AddEngine(engine, catalogue.Engine(engine.Name), holder, art);

                    break;

                case Capsule capsule:

                    AddModel(catalogue.Capsule(capsule.Name).fit, holder, Vector3.zero, art);

                    break;

                case Tank tank:

                    AddMesh(HullMesh.Tank(tank, below is Engine hung ? catalogue.Engine(hung.Name) : null), holder, art);

                    break;

                case Skirt skirt:

                    AddMesh(HullMesh.Skirt(skirt), holder, art);

                    break;

                case Decoupler ring:

                    AddMesh(HullMesh.Decoupler(ring), holder, art);

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

        foreach ((Engine engine, Transform pivot, ExhaustRenderer exhaust, Light light, float intensity) in _engines) {

            pivot.localRotation = Quaternion.FromToRotation(Vector3.up, ToScene(engine.Direction));
            exhaust.Strength = (float)engine.Chamber;

            if (light != null) {

                light.enabled = engine.Chamber > 0.01;
                light.intensity = intensity * (float)engine.Chamber;

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

    private void AddEngine(Engine engine, EngineEntry entry, Transform holder, VesselArt art) {

        float length = (float)engine.Length;
        Transform pivot = new GameObject("Gimbal").transform;

        pivot.SetParent(holder, false);
        pivot.localPosition = new Vector3(0.0f, length, 0.0f);

        AddModel(entry.fit, pivot, new Vector3(0.0f, -length, 0.0f), art);

        ExhaustRenderer exhaust = new ExhaustRenderer(art.Exhaust, pivot, entry);

        // The exit hangs a nozzle's length below the pivot; the exhaust's own +Y is downstream.
        exhaust.Place(new Vector3(0.0f, -length, 0.0f), Quaternion.Euler(180.0f, 0.0f, 0.0f));

        Light light = null;
        float intensity = 0.0f;

        // The exhaust lights its own bell from inside. URP's inverse square runs in scene units (km): a strength given at
        // a metre is a millionth of that at a kilometre.
        if (entry.light is { } glow) {

            light = new GameObject("Exhaust Light").AddComponent<Light>();
            light.transform.SetParent(pivot, false);
            light.transform.localPosition = new Vector3(0.0f, (float)(glow.height - engine.Length), 0.0f);
            light.type = LightType.Point;
            light.range = (float)(glow.range / MapSpace.MetresPerUnit);
            light.color = glow.colour is { Length: 3 } ? new Color((float)glow.colour[0], (float)glow.colour[1], (float)glow.colour[2]) : Color.white;
            light.shadows = LightShadows.None;
            light.enabled = false;
            intensity = (float)(glow.intensity / (MapSpace.MetresPerUnit * MapSpace.MetresPerUnit));

        }

        _engines.Add((engine, pivot, exhaust, light, intensity));

    }

    private static void AddModel(ModelFit fit, Transform parent, Vector3 origin, VesselArt art) {

        // The fit goes on a holder: an imported model's root carries its own turn and offset, which must stay.
        Transform holder = new GameObject("Fit").transform;

        holder.SetParent(parent, false);
        holder.localPosition = origin + HullMesh.Scene(fit.offset);
        holder.localRotation = Quaternion.AngleAxis((float)(-fit.turn * Mathf.Rad2Deg), Vector3.up);
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

    }

    private void AddMesh(Mesh mesh, Transform holder, VesselArt art) {

        _meshes.Add(mesh);
        holder.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        holder.gameObject.AddComponent<MeshRenderer>().sharedMaterials = art.Finishes;

    }

}

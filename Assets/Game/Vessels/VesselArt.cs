using System;

using UnityEngine;

namespace MaxQ.Game.Vessels;

/// <summary>Everything a vessel is drawn and heard with, wired by the project setup.</summary>
[Serializable]
public sealed class VesselArt {

    public TextAsset Catalogue;
    public TextAsset Craft;

    /// <summary>Imported models, found by file name from the catalogue's fits.</summary>
    public GameObject[] Models;

    /// <summary>The hull's finishes, in HullMesh's submesh order.</summary>
    public Material[] Finishes;

    /// <summary>An ablative heat shield, and window glass.</summary>
    public Material Shield;
    public Material Glass;

    /// <summary>Every RCS jet layer, its look set per renderer from the catalogue.</summary>
    public Material Plume;

    /// <summary>Every engine's exhaust volume, likewise.</summary>
    public Material Exhaust;

    /// <summary>Every radiatively cooled nozzle extension's glow.</summary>
    public Material NozzleGlow;

    public Material Finish(string name) => name switch {

        "paint" => Finishes[0],
        "metal" => Finishes[1],
        "dark" => Finishes[2],
        "shield" => Shield,
        "glass" => Glass,
        _ => throw new InvalidOperationException($"No vessel finish named '{name}'."),

    };

    public GameObject Model(string name) {

        foreach (GameObject model in Models ?? Array.Empty<GameObject>()) {

            if (model != null && model.name == name) {

                return model;

            }

        }

        throw new InvalidOperationException($"No vessel model named '{name}' is imported.");

    }

}

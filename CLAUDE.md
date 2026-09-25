# Max-Q

A sandbox rocket simulator in the spirit of Kerbal Space Program. Unity 6 (URP), C#, 3D,
patched conics, 1/5 Earth scale. Personal project, never distributed. Successor to
`../Full-Thrust` (Godot).

## Layout

- `Assets/Sim/` - assembly `MaxQ.Sim`, `noEngineReferences: true`. All physics and orbital
  mechanics. **No `UnityEngine` reference** - the compiler enforces it.
  - `Numerics/` vectors and maths types, `Orbits/` conics, patches, maneuvers and prediction,
    `Bodies/` celestial bodies and the system catalogue, `Vessels/` vessels, `Compatibility/` polyfills.
  - `Tests/` - assembly `MaxQ.Sim.Tests`, EditMode tests mirroring the sim folders.
- `Assets/Game/` - assembly `MaxQ.Game`. Rendering, input, UI. No physics.
  - `Map/` the map view (`Rendering/` bodies and lines, `Overlay/` HUD), `Diagnostics/` capture
    tooling, `Editor/` setup and build, `Scenes/`, `Settings/` (pipeline, materials, UI panel), `Art/`.
- `Assets/csc.rsp` raises C# to 10 (file-scoped namespaces); it must stay at the Assets root.
- `tools/` - `run.sh` runs the game without the editor, rebuilding the player first when sources
  changed (extra args go to the player); `build.sh` builds the player, or runs the sim tests with
  `--tests [results.xml]`. The engine path lives in `build.sh` (override with `UNITY=`).

Group by feature, not by file type: a feature's code, shader and stylesheet live together. When a
folder collects more than a handful of files, split it into subfolders. Namespaces mirror folders
(`MaxQ.Sim.Orbits`, `MaxQ.Game.Map.Rendering`); never name a folder after a type it would shadow
(`Math`, `Debug`).

Verify visual changes: `Max-Q > Rebuild Project Setup` when assets changed, then
`tools/run.sh -capture <dir>` writes scripted screenshots and exits.

## Quality Bar

No placeholders, no stubs, no grey boxes, no "good enough for now". Anything that lands is
finished within its written scope. **The scope is the guard rail**: state which visuals a piece of
work owns before starting it. Anything not listed waits its turn - no
"while I'm here" work on plumes, clouds, water or terrain. Prefer a built-in URP feature or
package over a hand-written shader; write custom rendering only when the built-in cannot meet
the bar, and say so first.

---

# Code Style

## C#

### Spacing and Braces

Every block delimited by `{...}` gets a blank line after the opening brace and before the closing
brace: method bodies, class and struct bodies, control-flow blocks, initializers, lambda bodies.

```csharp
public Vessel Stage(double time) {

    if (_stages.Count == 0) {

        return this;

    }

    Stage spent = _stages.Pop();

    return SpawnDebris(spent, time);

}
```

File-scoped namespaces (`namespace MaxQ.Sim;`). The only exceptions to the blank-line rule are
`using` groups and single-line members.

### Error Handling

Guard clauses with early returns, same blank-line pattern. Prefer returning a result or nullable
for expected conditions; exceptions for programmer error only.

### Declarations

- No horizontal alignment of types, names, values or `=` signs. Group related members with blank lines.
- One-line auto-properties and expression-bodied members stay on one line.
- `sealed` by default on classes; `readonly struct` for value types.
- No abstraction with a single implementation - no interface, factory, or config for one case.

### Usings

Groups, blank line between each: `System.*`, then `MaxQ.*`, then external (`UnityEngine`, packages).

### Numeric Conventions

The sim runs in doubles. Never a `float` in `MaxQ.Sim` - conversion to single precision happens
only at the render boundary in `MaxQ.Game`. Explicit decimal points (`9.81`, `1.0`), digit
separators for large magnitudes (`1_274_200.0`). SI units throughout; suffix ambiguous names
(`burnTimeSeconds`). No kilometres or degrees outside display code.

### Comments

Only when the *why* is non-obvious. One short line maximum. XML doc comments only on the public
surface of `MaxQ.Sim`, and keep them to one or two lines.

### Unity-Specific

- MonoBehaviours are thin: read input, read sim state, draw. They never integrate anything.
- `[SerializeField]` is for authored tuning values only, never simulation state.
- Component lookups resolved once in `Awake`, cached - no per-frame `GetComponent`/`Find`.
- Rendering positions are always relative to the floating origin; never feed a raw sim
  coordinate into a `Transform`.

### General

- No trailing whitespace, 4-space indentation, blank lines between statement groups.
- Debug output (screenshots, logs) goes to the scratchpad, never the repo.

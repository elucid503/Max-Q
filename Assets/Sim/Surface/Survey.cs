using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace MaxQ.Sim.Surface;

/// <summary>Owns a body's baked survey files, mapped into memory so only the pages the ground touches are read.</summary>
public sealed unsafe class Survey : IDisposable {

    private static readonly string[] TerraFiles = { "height.i16", "water.i16", "shore_distance.i16", "moisture.i16" };
    private static readonly string[] SeleneFiles = { "height.i16", "maria.i16", "steepness.i16" };

    private readonly MemoryMappedFile[] _files;
    private readonly MemoryMappedViewAccessor[] _views;

    public Terrain Terrain { get; }

    private Survey(string directory, string[] files, Func<IntPtr[], Terrain> terrain) {

        IntPtr[] data = new IntPtr[files.Length];

        _files = new MemoryMappedFile[files.Length];
        _views = new MemoryMappedViewAccessor[files.Length];

        for (int i = 0; i < files.Length; i++) {

            _files[i] = MemoryMappedFile.CreateFromFile(Path.Combine(directory, files[i]), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _views[i] = _files[i].CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte* pointer = null;

            _views[i].SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            data[i] = (IntPtr)(pointer + _views[i].PointerOffset);

        }

        Terrain = terrain(data);

    }

    /// <summary>Maps Terra's survey baked into <paramref name="directory"/> by tools/terra.sh; null if it has not been baked.</summary>
    public static Survey Terra(string directory, double radius) =>
        Open(directory, TerraFiles, data => new Terrain(data[0], data[1], data[2], data[3], radius));

    /// <summary>Maps Selene's survey baked into <paramref name="directory"/> by tools/selene.sh; null if it has not been baked.</summary>
    public static Survey Selene(string directory, double radius) =>
        Open(directory, SeleneFiles, data => Terrain.Cratered(data[0], data[1], data[2], radius));

    private static Survey Open(string directory, string[] files, Func<IntPtr[], Terrain> terrain) {

        foreach (string file in files) {

            if (!File.Exists(Path.Combine(directory, file))) {

                return null;

            }

        }

        return new Survey(directory, files, terrain);

    }

    public void Dispose() {

        for (int i = 0; i < _files.Length; i++) {

            _views[i].SafeMemoryMappedViewHandle.ReleasePointer();
            _views[i].Dispose();
            _files[i].Dispose();

        }

    }

}

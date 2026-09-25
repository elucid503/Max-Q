using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace MaxQ.Sim.Surface;

/// <summary>Owns the baked survey files, mapped into memory so only the pages the ground touches are read.</summary>
public sealed unsafe class Survey : IDisposable {

    private readonly MemoryMappedFile _elevationFile;
    private readonly MemoryMappedFile _waterFile;
    private readonly MemoryMappedViewAccessor _elevationView;
    private readonly MemoryMappedViewAccessor _waterView;

    public Terrain Terrain { get; }

    private Survey(string directory, double radius) {

        _elevationFile = MemoryMappedFile.CreateFromFile(Path.Combine(directory, "elevation.i16"), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _waterFile = MemoryMappedFile.CreateFromFile(Path.Combine(directory, "water.i16"), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _elevationView = _elevationFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _waterView = _waterFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* elevation = null;
        byte* water = null;

        _elevationView.SafeMemoryMappedViewHandle.AcquirePointer(ref elevation);
        _waterView.SafeMemoryMappedViewHandle.AcquirePointer(ref water);

        Terrain = new Terrain((IntPtr)(elevation + _elevationView.PointerOffset), (IntPtr)(water + _waterView.PointerOffset), radius);

    }

    /// <summary>Maps the survey baked into <paramref name="directory"/> by tools/terra.sh; null if it has not been baked.</summary>
    public static Survey Open(string directory, double radius) {

        if (!File.Exists(Path.Combine(directory, "elevation.i16")) || !File.Exists(Path.Combine(directory, "water.i16"))) {

            return null;

        }

        return new Survey(directory, radius);

    }

    public void Dispose() {

        _elevationView.SafeMemoryMappedViewHandle.ReleasePointer();
        _waterView.SafeMemoryMappedViewHandle.ReleasePointer();
        _elevationView.Dispose();
        _waterView.Dispose();
        _elevationFile.Dispose();
        _waterFile.Dispose();

    }

}

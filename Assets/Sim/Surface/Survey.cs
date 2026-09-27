using System;
using System.IO;
using System.IO.MemoryMappedFiles;

using MaxQ.Sim.Ocean;

namespace MaxQ.Sim.Surface;

/// <summary>Owns the baked survey files, mapped into memory so only the pages the ground and sea touch are read.</summary>
public sealed unsafe class Survey : IDisposable {

    private static readonly string[] Files = { "elevation.i16", "levels.i16", "shore.i16", "fetch.u8", "rivers.bin", "sea_state.bin" };

    private readonly MemoryMappedFile[] _files = new MemoryMappedFile[Files.Length];
    private readonly MemoryMappedViewAccessor[] _views = new MemoryMappedViewAccessor[Files.Length];

    public Terrain Terrain { get; }

    public SeaState SeaState { get; }

    private Survey(string directory, double radius) {

        IntPtr[] data = new IntPtr[Files.Length];

        for (int i = 0; i < Files.Length; i++) {

            _files[i] = MemoryMappedFile.CreateFromFile(Path.Combine(directory, Files[i]), FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _views[i] = _files[i].CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte* pointer = null;

            _views[i].SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            data[i] = (IntPtr)(pointer + _views[i].PointerOffset);

        }

        Terrain = new Terrain(data[0], data[1], data[2], data[3], new Rivers(data[4]), radius);
        SeaState = new SeaState(data[5]);

    }

    /// <summary>Maps the survey baked into <paramref name="directory"/> by tools/terra.sh; null if it has not been baked.</summary>
    public static Survey Open(string directory, double radius) {

        foreach (string file in Files) {

            if (!File.Exists(Path.Combine(directory, file))) {

                return null;

            }

        }

        return new Survey(directory, radius);

    }

    public void Dispose() {

        for (int i = 0; i < Files.Length; i++) {

            _views[i].SafeMemoryMappedViewHandle.ReleasePointer();
            _views[i].Dispose();
            _files[i].Dispose();

        }

    }

}

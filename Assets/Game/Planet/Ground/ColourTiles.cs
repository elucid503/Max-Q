using System;
using System.Collections.Generic;
using System.IO;

using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Streams the satellite colour pyramid (colour.tiles, BC7) from disk into a cache of tile textures.</summary>
public sealed unsafe class ColourTiles : IDisposable {

    public const int Levels = 7;
    public const int Core = 248;
    public const int Border = 4;
    public const int Texels = Core + 2 * Border;

    public const int HeaderBytes = 16;
    public const uint Magic = 0x5451584D;

    // The coarse levels are small and cover the whole planet; they stay resident so there is always a fallback.
    private const int PinnedLevels = 3;
    private const int Capacity = 900;
    private const int ReadsInFlight = 24;
    private const int UploadsPerFrame = 24;

    private sealed class Tile {

        public Texture2D Texture;
        public bool Ready;
        public int LastUsed;
        public ReadHandle Read;
        public NativeArray<ReadCommand> Command;
        public NativeArray<byte> Buffer;

    }

    private readonly string _path;
    private readonly long _recordBytes;

    private readonly Dictionary<long, Tile> _tiles = new Dictionary<long, Tile>();
    private readonly List<(long Key, Tile Tile)> _reading = new List<(long, Tile)>();
    private readonly Queue<long> _wanted = new Queue<long>();
    private readonly HashSet<long> _queued = new HashSet<long>();
    private readonly Stack<Texture2D> _spareTextures = new Stack<Texture2D>();
    private readonly Stack<NativeArray<byte>> _spareBuffers = new Stack<NativeArray<byte>>();
    private readonly List<long> _evict = new List<long>();

    private int _frame;

    public int Pending => _reading.Count + _wanted.Count;

    public ColourTiles(string path) {

        _path = path;

        using (BinaryReader header = new BinaryReader(File.OpenRead(path))) {

            if (header.ReadUInt32() != Magic || header.ReadInt32() != Texels || header.ReadInt32() != Levels) {

                throw new InvalidDataException($"{path} is not a colour tile pyramid for this build; rebake with tools/terra.sh");

            }

            _recordBytes = header.ReadInt32();

        }

        for (int face = 0; face < 6; face++) {

            for (int level = 0; level < PinnedLevels; level++) {

                for (int y = 0; y < 1 << level; y++) {

                    for (int x = 0; x < 1 << level; x++) {

                        Want(Key(face, level, x, y));

                    }

                }

            }

        }

    }

    public static long Key(int face, int level, int x, int y) {

        long before = 0;

        for (int l = 0; l < level; l++) {

            before += 1L << (2 * l);

        }

        return face * ((1L << (2 * Levels)) - 1) / 3 + before + ((long)y << level) + x;

    }

    /// <summary>The finest loaded tile covering node (face, depth, x, y), asking for the one it should have. Level
    /// says which pyramid level the returned tile is.</summary>
    public Texture2D Resolve(int face, int depth, int x, int y, out int level) {

        int wantLevel = Math.Min(depth, Levels - 1);

        for (level = wantLevel; level >= 0; level--) {

            int shift = depth - level;
            long key = Key(face, level, x >> shift, y >> shift);

            if (_tiles.TryGetValue(key, out Tile tile) && tile.Ready) {

                tile.LastUsed = _frame;

                if (level < wantLevel) {

                    Want(Key(face, wantLevel, x >> (depth - wantLevel), y >> (depth - wantLevel)));

                }

                return tile.Texture;

            }

        }

        level = 0;
        Want(Key(face, 0, 0, 0));

        return null;

    }

    /// <summary>Loads everything pinned before the first frame draws.</summary>
    public void LoadPinned() {

        while (Pending > 0) {

            Update();

        }

    }

    public void Update() {

        _frame++;

        int uploads = 0;

        for (int i = _reading.Count - 1; i >= 0 && uploads < UploadsPerFrame; i--) {

            (long key, Tile tile) = _reading[i];

            if (tile.Read.Status == ReadStatus.InProgress) {

                continue;

            }

            if (tile.Read.Status != ReadStatus.Complete) {

                throw new IOException($"reading colour tile {key} from {_path} failed");

            }

            tile.Read.Dispose();
            tile.Command.Dispose();
            tile.Texture.LoadRawTextureData(tile.Buffer);
            tile.Texture.Apply(false, false);
            tile.Ready = true;
            tile.LastUsed = _frame;

            _spareBuffers.Push(tile.Buffer);
            tile.Buffer = default;
            _reading.RemoveAt(i);
            uploads++;

        }

        while (_reading.Count < ReadsInFlight && _wanted.Count > 0) {

            long key = _wanted.Dequeue();

            _queued.Remove(key);
            StartRead(key);

        }

        if (_tiles.Count > Capacity) {

            Evict();

        }

    }

    private void Want(long key) {

        if (_tiles.ContainsKey(key) || !_queued.Add(key)) {

            return;

        }

        _wanted.Enqueue(key);

    }

    private void StartRead(long key) {

        Tile tile = new Tile {

            Texture = _spareTextures.Count > 0 ? _spareTextures.Pop() : CreateTexture(),
            Buffer = _spareBuffers.Count > 0 ? _spareBuffers.Pop() : new NativeArray<byte>((int)_recordBytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            LastUsed = _frame,

        };

        // The command must outlive the read, so it lives beside the tile until the read completes.
        tile.Command = new NativeArray<ReadCommand>(1, Allocator.Persistent);
        tile.Command[0] = new ReadCommand {

            Offset = HeaderBytes + key * _recordBytes,
            Size = _recordBytes,
            Buffer = tile.Buffer.GetUnsafePtr(),

        };

        tile.Read = AsyncReadManager.Read(_path, (ReadCommand*)tile.Command.GetUnsafePtr(), 1);
        _tiles[key] = tile;
        _reading.Add((key, tile));

    }

    private static Texture2D CreateTexture() => new Texture2D(Texels, Texels, TextureFormat.BC7, true, false) {

        name = "Colour Tile",
        wrapMode = TextureWrapMode.Clamp,
        filterMode = FilterMode.Trilinear,
        anisoLevel = 8,

    };

    // Least recently used first; pinned levels and tiles still loading stay.
    private void Evict() {

        _evict.Clear();

        foreach (KeyValuePair<long, Tile> pair in _tiles) {

            if (pair.Value.Ready && pair.Value.LastUsed < _frame - 1 && !Pinned(pair.Key)) {

                _evict.Add(pair.Key);

            }

        }

        _evict.Sort((a, b) => _tiles[a].LastUsed.CompareTo(_tiles[b].LastUsed));

        for (int i = 0; i < _evict.Count && _tiles.Count > Capacity; i++) {

            _spareTextures.Push(_tiles[_evict[i]].Texture);
            _tiles.Remove(_evict[i]);

        }

    }

    private static bool Pinned(long key) => key % (((1L << (2 * Levels)) - 1) / 3) < ((1L << (2 * PinnedLevels)) - 1) / 3;

    public void Dispose() {

        foreach ((long _, Tile tile) in _reading) {

            tile.Read.JobHandle.Complete();
            tile.Read.Dispose();
            tile.Command.Dispose();
            tile.Buffer.Dispose();

        }

        while (_spareBuffers.Count > 0) {

            _spareBuffers.Pop().Dispose();

        }

        foreach (Tile tile in _tiles.Values) {

            UnityEngine.Object.Destroy(tile.Texture);

        }

        while (_spareTextures.Count > 0) {

            UnityEngine.Object.Destroy(_spareTextures.Pop());

        }

    }

}

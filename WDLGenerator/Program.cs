using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WDLGenerator
{
    class Program
    {
        #region Constants
        public const float TileSize = 533f + 1f / 3f;
        public const float ChunkSize = TileSize / 16f;
        public const float HChunkSize = ChunkSize / 2f;
        public const float UnitSize = ChunkSize / 8f;
        public static readonly int MCNKSize = Marshal.SizeOf<MCNK>();
        #endregion

        private static string OutputDirectory;
        private static Dictionary<string, string> ADTs;

        static void Main(string[] args)
        {
            if (!Directory.Exists(args[0]))
                LogAndExit("Directory not found.");

            OutputDirectory = args[0];
            ADTs = new Dictionary<string, string>();

            string continent = null;
            var regex = new Regex(@"(.*)_(\d+_\d+)\.adt", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // load the ADT lookup
            foreach (var file in Directory.EnumerateFiles(args[0], "*.adt"))
            {
                var match = regex.Match(Path.GetFileName(file));
                if (!match.Success)
                    continue;

                if (continent == null)
                    continent = match.Groups[1].Value;

                ADTs[match.Groups[2].Value] = file;
            }

            if (ADTs.Count == 0)
                LogAndExit("No valid ADTs found.");

            GenerateWDL(continent);
        }
        
        #region WDL Generation

        private static void GenerateWDL(string continent)
        {
            var mapAreaOffsets = new int[4096];
            var heightmaps = new List<short[]>(ADTs.Count);

            string curADT;
            int curOffset = 20 + 4096 * 4; // MVER + MAOF chunks

            for (int i = 0; i < 4096; i++)
            {
                curADT = $"{i % 64}_{i / 64}";

                Log($"Processing {curADT.PadRight(5, ' ')} - {(i / 4095f).ToString("P")}");

                if (TryGetHeightMap(curADT, out var heightmap))
                {
                    heightmaps.Add(heightmap);
                    mapAreaOffsets[i] = curOffset;
                    curOffset += heightmap.Length * 2 + 8; // prev MARE size
                }
            }

            string filename = Path.Combine(OutputDirectory, continent + ".wdl");
            using (var fs = File.Create(filename))
            using (var bw = new BinaryWriter(fs))
            {
                // MVER
                bw.Write(0x4D564552u);
                bw.Write(4);
                bw.Write(18);

                // MAOF
                bw.Write(0x4D414F46u);
                bw.Write(mapAreaOffsets.Length * 4);
                bw.WriteArray(mapAreaOffsets);

                // MARE
                foreach (var heightmap in heightmaps)
                {
                    bw.Write(0x4D415245u);
                    bw.Write(heightmap.Length * 2);
                    bw.WriteArray(heightmap);
                }
            }
        }

        private static bool TryGetHeightMap(string curADT, out short[] heightmap)
        {
            heightmap = new short[17 * 17 + 16 * 16];

            // check we have an ADT for the offset
            if (!ADTs.TryGetValue(curADT, out string filePath))
                return false;

            var heights = new float[256][];
            long MCNKPos, MCNKEnd;

            // read all chunk heights
            using (var fs = File.OpenRead(filePath))
            using (var br = new BinaryReader(fs))
            {
                for (int i = 0; i < 256; i++)
                {
                    // only broken ADTs will not have all 256 MCNKs
                    if (!SeekChunk(br, 0x4D434E4B, out int mcnksize))
                    {
                        Log($"{Path.GetFileName(filePath)} is malformed.", ConsoleColor.Yellow);
                        return false;
                    }

                    MCNKPos = br.BaseStream.Position;
                    MCNKEnd = MCNKPos + mcnksize - MCNKSize;

                    // extract the chunk's base height
                    float baseHeight = br.Read<MCNK>(MCNKSize).Position.Z;

                    // seek the chunk's MCVT; zeroed if missing
                    if (!SeekChunk(br, 0x4D435654, out _, MCNKEnd))
                        continue;

                    // read the heights and apply the base
                    heights[i] = br.ReadArray<float>(145).ToArray();
                    for (int j = 0; j < heights[i].Length; j++)
                        heights[i][j] += baseHeight;

                    // skip to the next MCNK
                    br.BaseStream.Position = MCNKPos + mcnksize;
                }
            }

            // calculate the heightmap as a short array
            float x, y;
            for (int i = 0; i < 17; i++)
            {
                for (int j = 0; j < 17; j++)
                {
                    // outer - correct
                    x = j * ChunkSize;
                    y = i * ChunkSize;
                    heightmap[i * 17 + j] = GetHeight(heights, x, y);

                    // inner - close enough; correct values appear to use some form of averaging
                    if (i < 16 && j < 16)
                        heightmap[17 * 17 + i * 16 + j] = GetHeight(heights, x + HChunkSize, y + HChunkSize);
                }
            }

            return true;
        }

        private static short GetHeight(float[][] heights, float x, float y)
        {
            int cx = Math.Min(Math.Max((int)(x / ChunkSize), 0), 15);
            int cy = Math.Min(Math.Max((int)(y / ChunkSize), 0), 15);

            if (heights[cy * 16 + cx] == null)
                return 0;

            x -= cx * ChunkSize;
            y -= cy * ChunkSize;

            int row = (int)(y / (UnitSize * 0.5f) + 0.5f);
            int col = (int)((x - UnitSize * 0.5f * (row % 2)) / UnitSize + 0.5f);
            bool inner = (row % 2) == 1;

            if (row < 0 || col < 0 || row > 16 || col > (inner ? 8 : 9))
                return 0;

            // truncate and clamp the float value
            float height = heights[cy * 16 + cx][17 * (row / 2) + (inner ? 9 : 0) + col];
            return (short)Math.Min(Math.Max(height, short.MinValue), short.MaxValue);
        }

        #endregion

        #region Helpers

        private static void Log(string text, ConsoleColor color = ConsoleColor.White)
        {
            if (Console.ForegroundColor != color)
                Console.ForegroundColor = color;

            Console.WriteLine(text);
        }

        private static void LogAndExit(string text)
        {
            Log(text, ConsoleColor.Red);
            Log("Press Enter to exit.", ConsoleColor.Red);
            Console.ReadLine();
            Environment.Exit(0);
        }

        private static bool SeekChunk(BinaryReader br, uint token, out int chunkSize, long length = -1)
        {
            if (length < 0)
                length = br.BaseStream.Length;

            uint ctoken;
            while (br.BaseStream.Position < length)
            {
                ctoken = br.ReadUInt32();
                chunkSize = br.ReadInt32();
                if (ctoken == token)
                    return true;

                br.BaseStream.Position += chunkSize; // skip chunk
            }

            chunkSize = 0;
            return false;
        }

        #endregion
    }

    #region Extensions

    static class Extensions
    {
        public static Span<T> ReadArray<T>(this BinaryReader br, int count) where T : unmanaged
        {
            byte[] buffer = br.ReadBytes(count * Marshal.SizeOf<T>());
            return MemoryMarshal.Cast<byte, T>(buffer);
        }

        public static T Read<T>(this BinaryReader br, int size) where T : unmanaged
        {
            return MemoryMarshal.Read<T>(br.ReadBytes(size));
        }

        public static void WriteArray<T>(this BinaryWriter bw, T[] array) where T : unmanaged
        {
            bw.Write(MemoryMarshal.AsBytes<T>(array));
        }
    }

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    struct MCNK
    {
        public uint Flags;
        public int IndexX;
        public int IndexY;
        public int NumLayers;
        public int NumDoodadRefs;
        public int MCVT;
        public int MCNR;
        public int MCLY;
        public int MCRF;
        public int MCAL;
        public int SizeAlpha;
        public int MCSH;
        public int SizeShadow;
        public int AreaId;
        public int NumMapObjRefs;
        public int Holes;
        public ulong Low1;
        public ulong Low2;
        public int PredTex;
        public int NoEffectDoodad;
        public int MCSE;
        public int NumSoundEmitters;
        public int MCLQ;
        public int SizeLiquid;
        public Vector3 Position;
        public int MCCV;
        public int MCLV;
        public int Unused;
    }

    #endregion
}

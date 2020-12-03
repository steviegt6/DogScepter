﻿using System;
using System.Collections.Generic;
using System.Text;

using DogScepterLib.Core.Chunks;

namespace DogScepterLib.Core
{
    public class GMChunk : GMSerializable
    {
        public int Length;
        public int StartOffset;
        public int EndOffset;

        public virtual void Serialize(GMDataWriter writer)
        {
        }

        public virtual void Unserialize(GMDataReader reader)
        {
            // Read chunk length, measure start/end
            Length = reader.ReadInt32();
            StartOffset = reader.Offset;
            EndOffset = StartOffset + Length;
        }
    }

    public class GMChunkFORM : GMChunk
    {
        public List<string> ChunkNames;
        public Dictionary<string, GMChunk> Chunks;

        public static readonly Dictionary<string, Type> ChunkMap = new Dictionary<string, Type>()
        {
            { "GEN8", typeof(GMChunkGEN8) },
            { "OPTN", typeof(GMChunkOPTN) },
            { "LANG", typeof(GMChunkLANG) },
            { "EXTN", typeof(GMChunkEXTN) },
            { "STRG", typeof(GMChunkSTRG) },
            { "TXTR", typeof(GMChunkTXTR) },
            { "TPAG", typeof(GMChunkTPAG) }
        };

        public override void Serialize(GMDataWriter writer)
        {
            base.Serialize(writer);

            int beg = writer.BeginLength();

            // Write all the sub-chunks
            for (int i = 0; i < ChunkNames.Count; i++)
            {
                GMChunk chunk;
                if (Chunks.TryGetValue(ChunkNames[i], out chunk))
                {
                    // Write chunk name, length, and content
                    writer.Write(ChunkNames[i].ToCharArray());
                    int chunkBeg = writer.BeginLength();
                    chunk.Serialize(writer);

                    // If not the last chunk, apply 16-byte padding if necessary
                    if (writer.Data.VersionInfo.AlignChunksTo16 && i != (ChunkNames.Count - 1))
                        writer.Pad(16);

                    writer.EndLength(chunkBeg);
                }
            }

            writer.EndLength(beg);
        }

        public override void Unserialize(GMDataReader reader)
        {
            base.Unserialize(reader);

            // Gather the names and offsets of the sub-chunks
            ChunkNames = new List<string>();
            List<int> ChunkOffsets = new List<int>();
            while (reader.Offset < EndOffset)
            {
                // Read its name and skip contents
                ChunkOffsets.Add(reader.Offset);
                string name = reader.ReadChars(4);
                ChunkNames.Add(name);
                int length = reader.ReadInt32();
                reader.Offset += length;

                // Check if this is a GMS 2.3+ file
                if (name == "SEQN")
                    reader.Data.VersionInfo.SetNumber(2, 3);

                // Update whether this version aligns chunks to 16 bytes
                if (reader.Offset < EndOffset)
                    reader.Data.VersionInfo.AlignChunksTo16 &= (reader.Offset % 16 == 0);
            }

            // Actually read all the sub-chunks
            Chunks = new Dictionary<string, GMChunk>();
            reader.Offset = StartOffset;
            for (int i = 0; i < ChunkNames.Count; i++)
            {
                reader.Offset = ChunkOffsets[i];

                Type type;
                if (!ChunkMap.TryGetValue(ChunkNames[i], out type))
                {
                    // Unknown chunk name, so skip past it
                    reader.Warnings.Add(new GMWarning($"Unknown chunk with name {ChunkNames[i]}", 
                                                        GMWarning.WarningLevel.Severe, GMWarning.WarningKind.UnknownChunk));
                    continue;
                }

                // Actually parse the chunk, starting at its length
                reader.Offset += 4;
                GMChunk chunk = (GMChunk)Activator.CreateInstance(type);
                chunk.Unserialize(reader);
                Chunks.Add(ChunkNames[i], chunk);
            }
        }
    }
}

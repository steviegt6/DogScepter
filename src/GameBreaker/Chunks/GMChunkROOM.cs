﻿using System;
using System.Collections.Generic;
using System.Text;
using GameBreaker.Models;
using static GameBreaker.Models.GMRoom;

namespace GameBreaker.Chunks
{
    public class GMChunkROOM : GMChunk
    {
        public GMUniquePointerList<GMRoom> List;

        public override void Serialize(GMDataWriter writer)
        {
            base.Serialize(writer);

            List.Serialize(writer);
        }

        public override void Deserialize(GMDataReader reader)
        {
            base.Deserialize(reader);

            DoFormatCheck(reader);

            List = new GMUniquePointerList<GMRoom>();
            List.Deserialize(reader);
        }

        private static void DoFormatCheck(GMDataReader reader)
        {
            // Do a length check on one of the layers to see if this is 2022.1 or higher
            if (reader.VersionInfo.IsVersionAtLeast(2, 3) && !reader.VersionInfo.IsVersionAtLeast(2022))
            {
                int returnTo = reader.Offset;

                // Iterate over all rooms until a length check is performed
                int roomCount = reader.ReadInt32();
                bool finished = false;
                for (int roomIndex = 0; roomIndex < roomCount && !finished; roomIndex++)
                {
                    // Advance to room data we're interested in (and grab pointer for next room)
                    reader.Offset = returnTo + 4 + (4 * roomIndex);
                    int roomPtr = reader.ReadInt32();
                    reader.Offset = roomPtr + (22 * 4);

                    // Get the pointer for this room's layer list, as well as pointer to sequence list
                    int layerListPtr = reader.ReadInt32();
                    int seqnPtr = reader.ReadInt32();
                    reader.Offset = layerListPtr;
                    int layerCount = reader.ReadInt32();
                    if (layerCount >= 1)
                    {
                        // Get pointer into the individual layer data (plus 8 bytes) for the first layer in the room
                        int jumpOffset = reader.ReadInt32() + 8;

                        // Find the offset for the end of this layer
                        int nextOffset;
                        if (layerCount == 1)
                            nextOffset = seqnPtr;
                        else
                            nextOffset = reader.ReadInt32(); // (pointer to next element in the layer list)

                        // Actually perform the length checks, depending on layer data
                        reader.Offset = jumpOffset;
                        switch ((GMRoom.Layer.LayerKind)reader.ReadInt32())
                        {
                            case GMRoom.Layer.LayerKind.Background:
                                if (nextOffset - reader.Offset > 16 * 4)
                                    reader.VersionInfo.SetVersion(2022, 1);
                                finished = true;
                                break;
                            case GMRoom.Layer.LayerKind.Instances:
                                reader.Offset += 6 * 4;
                                int instanceCount = reader.ReadInt32();
                                if (nextOffset - reader.Offset != (instanceCount * 4))
                                    reader.VersionInfo.SetVersion(2022, 1);
                                finished = true;
                                break;
                            case GMRoom.Layer.LayerKind.Assets:
                                reader.Offset += 6 * 4;
                                int tileOffset = reader.ReadInt32();
                                if (tileOffset != reader.Offset + 8)
                                    reader.VersionInfo.SetVersion(2022, 1);
                                finished = true;
                                break;
                            case GMRoom.Layer.LayerKind.Tiles:
                                reader.Offset += 7 * 4;
                                int tileMapWidth = reader.ReadInt32();
                                int tileMapHeight = reader.ReadInt32();
                                if (nextOffset - reader.Offset != (tileMapWidth * tileMapHeight * 4))
                                    reader.VersionInfo.SetVersion(2022, 1);
                                finished = true;
                                break;
                            case GMRoom.Layer.LayerKind.Effect:
                                reader.Offset += 7 * 4;
                                int propertyCount = reader.ReadInt32();
                                if (nextOffset - reader.Offset != (propertyCount * 3 * 4))
                                    reader.VersionInfo.SetVersion(2022, 1);
                                finished = true;
                                break;
                        }
                    }
                }

                reader.Offset = returnTo;
            }
        }
    }
}

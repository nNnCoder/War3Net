﻿// ------------------------------------------------------------------------------
// <copyright file="BuffObjectData.cs" company="Drake53">
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.
// </copyright>
// ------------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;

using War3Net.Build.Extensions;
using War3Net.Common.Extensions;

namespace War3Net.Build.Object
{
    public abstract class BuffObjectData
    {
        internal BuffObjectData(ObjectDataFormatVersion formatVersion)
        {
            FormatVersion = formatVersion;
        }

        internal BuffObjectData(BinaryReader reader)
        {
            ReadFrom(reader);
        }

        public ObjectDataFormatVersion FormatVersion { get; set; }

        public List<SimpleObjectModification> BaseBuffs { get; init; } = new();

        public List<SimpleObjectModification> NewBuffs { get; init; } = new();

        internal void ReadFrom(BinaryReader reader)
        {
            FormatVersion = reader.ReadInt32<ObjectDataFormatVersion>();

            nint baseBuffsCount = reader.ReadInt32();
            for (nint i = 0; i < baseBuffsCount; i++)
            {
                BaseBuffs.Add(reader.ReadSimpleObjectModification(FormatVersion));
            }

            nint newBuffsCount = reader.ReadInt32();
            for (nint i = 0; i < newBuffsCount; i++)
            {
                NewBuffs.Add(reader.ReadSimpleObjectModification(FormatVersion));
            }
        }

        internal void WriteTo(BinaryWriter writer)
        {
            writer.Write((int)FormatVersion);

            writer.Write(BaseBuffs.Count);
            foreach (var buff in BaseBuffs)
            {
                writer.Write(buff, FormatVersion);
            }

            writer.Write(NewBuffs.Count);
            foreach (var buff in NewBuffs)
            {
                writer.Write(buff, FormatVersion);
            }
        }
    }
}
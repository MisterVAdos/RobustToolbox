using System;
using System.Collections.Generic;

namespace Robust.Client.Graphics.Clyde
{
    internal partial class Clyde
    {
        private Texture? _configuredGridDecorAtlasTexture;
        private int _configuredGridDecorAtlasColumns;
        private int _configuredGridDecorAtlasRows;
        private int[] _configuredGridDecorRowsByTileType = Array.Empty<int>();

        public void SetGridDecorConfiguration(
            Texture atlasTexture,
            int atlasColumns,
            int atlasRows,
            IReadOnlyList<int> tileRows)
        {
            ArgumentNullException.ThrowIfNull(atlasTexture);
            ArgumentNullException.ThrowIfNull(tileRows);

            if (atlasColumns <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(atlasColumns),
                    atlasColumns,
                    "Grid decor atlas must contain at least one column.");

            if (atlasRows <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(atlasRows),
                    atlasRows,
                    "Grid decor atlas must contain at least one row.");

            var rows = new int[tileRows.Count];

            for (var i = 0; i < tileRows.Count; i++)
            {
                var row = tileRows[i];

                if (row < -1 || row >= atlasRows)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(tileRows),
                        row,
                        $"Grid decor row for tile type {i} must be -1 or between 0 and {atlasRows - 1}.");
                }

                rows[i] = row;
            }

            _configuredGridDecorAtlasTexture = atlasTexture;
            _configuredGridDecorAtlasColumns = atlasColumns;
            _configuredGridDecorAtlasRows = atlasRows;
            _configuredGridDecorRowsByTileType = rows;

            MarkAllGridDecorDirty();
        }

        public void ClearGridDecorConfiguration()
        {
            _configuredGridDecorAtlasTexture = null;
            _configuredGridDecorAtlasColumns = 0;
            _configuredGridDecorAtlasRows = 0;
            _configuredGridDecorRowsByTileType = Array.Empty<int>();

            MarkAllGridDecorDirty();
        }

        private void MarkAllGridDecorDirty()
        {
            foreach (var gridData in _mapChunkData.Values)
            {
                foreach (var chunk in gridData.Values)
                {
                    chunk.DecorDirty = true;
                }
            }
        }
    }
}

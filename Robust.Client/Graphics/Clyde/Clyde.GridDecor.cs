using System;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Robust.Client.Graphics.Clyde
{
    internal partial class Clyde
    {

        private void DrawGridDecor(MapChunkData datum, GLShaderProgram gridProgram)
        {
            if (datum.DecorCount <= 0 ||
                _configuredGridDecorAtlasTexture is not { } gridDecorTexture)
            {
                return;
            }

            SetTexture(TextureUnit.Texture0, gridDecorTexture);
            SetupGlobalUniformsImmediate(gridProgram, (ClydeTexture) gridDecorTexture);
            gridProgram.SetUniformTextureMaybe(UniIMainTexture, TextureUnit.Texture0);
            gridProgram.SetUniformTextureMaybe(UniILightTexture, TextureUnit.Texture1);
            gridProgram.SetUniform(UniIModUV, new Vector4(0, 0, 1, 1));

            gridProgram.SetUniform("gridDecorWindEnabled", 1f);
            gridProgram.SetUniform("gridDecorWindTime", _gridDecorTime);
            gridProgram.SetUniform("gridDecorWindPower", _gridDecorWindPower);
            gridProgram.SetUniform("gridDecorWindDirection", _gridDecorWindDirectionRadians);
            gridProgram.SetUniform("gridDecorWindGust", _gridDecorWindGustPower);

            BindVertexArray(datum.DecorVAO);
            CheckGlError();

            _debugStats.LastGLDrawCalls += 1;

            GL.DrawElements(
                GetQuadGLPrimitiveType(),
                datum.DecorCount * GetQuadBatchIndexCount(),
                DrawElementsType.UnsignedShort,
                0);

            CheckGlError();

            gridProgram.SetUniform("gridDecorWindEnabled", 0f);

            SetTexture(TextureUnit.Texture0, _tileDefinitionManager.TileTextureAtlas);
            SetupGlobalUniformsImmediate(gridProgram, (ClydeTexture) _tileDefinitionManager.TileTextureAtlas);
            gridProgram.SetUniformTextureMaybe(UniIMainTexture, TextureUnit.Texture0);
            gridProgram.SetUniformTextureMaybe(UniILightTexture, TextureUnit.Texture1);
            gridProgram.SetUniform(UniIModUV, new Vector4(0, 0, 1, 1));
        }

        private void InitializeGridDecorBuffers(
            Entity<MapGridComponent> grid,
            MapChunk chunk,
            MapChunkData datum,
            int vboSize,
            int eboSize)
        {
            var decorVao = GenVertexArray();
            BindVertexArray(decorVao);
            CheckGlError();

            var decorVbo = new GLBuffer(
                this,
                BufferTarget.ArrayBuffer,
                BufferUsageHint.DynamicDraw,
                vboSize * 64,
                $"Grid {grid.Owner} chunk {chunk.Indices} GridDecorVBO");

            var decorEbo = new GLBuffer(
                this,
                BufferTarget.ElementArrayBuffer,
                BufferUsageHint.DynamicDraw,
                eboSize * 64,
                $"Grid {grid.Owner} chunk {chunk.Indices} GridDecorEBO");

            ObjectLabelMaybe(
                ObjectLabelIdentifier.VertexArray,
                decorVao,
                $"Grid {grid.Owner} chunk {chunk.Indices} GridDecorVAO");

            SetupVAOLayout();
            CheckGlError();

            decorVbo.Use();
            decorEbo.Use();

            datum.DecorEBO = decorEbo;
            datum.DecorVBO = decorVbo;
            datum.DecorVAO = decorVao;
        }

        private void DeleteGridDecorBuffers(MapChunkData data)
        {
            DeleteVertexArray(data.DecorVAO);
            CheckGlError();
            data.DecorVBO.Delete();
            data.DecorEBO.Delete();
        }

        private static void MarkGridDecorDirty(MapChunkData data)
        {
            data.DecorDirty = true;
        }

        private sealed partial class MapChunkData
        {
            public bool DecorDirty = true;

            public uint DecorVAO;
            public GLBuffer DecorVBO = default!;
            public GLBuffer DecorEBO = default!;
            public int DecorCount;
        }

        public void SetGridDecorWind(
            float power,
            float directionRadians,
            float gustPower)
        {
            _gridDecorWindPower = Math.Clamp(power, 0f, 1f);
            _gridDecorWindDirectionRadians = directionRadians;
            _gridDecorWindGustPower = Math.Clamp(gustPower, 0f, 1f);
        }

        private bool HasGridDecor(Tile tile)
        {
            return GetGridDecorRow(tile) != null;
        }

        private int? GetGridDecorRow(Tile tile)
        {
            if (tile.IsEmpty)
                return null;

            if (tile.TypeId >= _configuredGridDecorRowsByTileType.Length)
                return null;

            var row = _configuredGridDecorRowsByTileType[tile.TypeId];

            return row >= 0 ? row : null;
        }

        private bool HasGridDecorAt(MapGridComponent grid, float x, float y)
        {
            var maps = _entityManager.System<SharedMapSystem>();
            var tilePos = new Vector2i((int)MathF.Floor(x), (int)MathF.Floor(y));

            if (!maps.TryGetTile(grid, tilePos, out var tile))
                return false;

            return HasGridDecor(tile);
        }

        private void _updateChunkDecor(
            Entity<MapGridComponent> grid,
            MapChunk chunk,
            MapChunkData datum)
        {
            const int PatchSize = 2;
            const int DecorMeshMultiplier = 64;

            Span<ushort> indexBuffer = EnsureSize(ref _chunkMeshBuilderIndexBuffer, _indicesPerChunk(chunk) * DecorMeshMultiplier);
            Span<Vertex2D> vertexBuffer = EnsureSize(ref _chunkMeshBuilderVertexBuffer, _verticesPerChunk(chunk) * DecorMeshMultiplier);

            var i = 0;
            var chunkSize = grid.Comp.ChunkSize;
            var chunkOriginScaled = chunk.Indices * chunkSize;

            for (ushort x = 0; x < chunkSize; x += PatchSize)
            {
                for (ushort y = 0; y < chunkSize; y += PatchSize)
                {
                    var tile = chunk.GetTile(x, y);
                    var decorRow = GetGridDecorRow(tile);

                    if (decorRow == null)
                        continue;

                    var gridX = x + chunkOriginScaled.X;
                    var gridY = y + chunkOriginScaled.Y;

                    var patchSeed =
                        GridDecorHash01(
                            chunk.Indices.X * 100 + x,
                            chunk.Indices.Y * 100 + y);

                    var clusterCount = 3 + (int)(patchSeed * 5f);

                    for (var cluster = 0; cluster < clusterCount; cluster++)
                    {
                        if ((i + 32) * 4 >= vertexBuffer.Length ||
                            (i + 32) * GetQuadBatchIndexCount() >= indexBuffer.Length)
                            break;

                        var region = GetGridDecorAtlasRegion(
                            decorRow.Value,
                            gridX * 92821 + gridY * 68917 + cluster * 193);

                        i = WriteGridDecorTextureClumpToBuffers(
                            i,
                            grid.Comp,
                            gridX,
                            gridY,
                            PatchSize,
                            vertexBuffer,
                            indexBuffer,
                            region,
                            cluster);
                    }
                }
            }

            var indexSlice = indexBuffer[..(i * GetQuadBatchIndexCount())];
            var vertSlice = vertexBuffer[..(i * 4)];

            GL.BindVertexArray(datum.DecorVAO);
            CheckGlError();

            datum.DecorEBO.Use();
            datum.DecorVBO.Use();

            datum.DecorEBO.Reallocate(indexSlice);
            datum.DecorVBO.Reallocate(vertSlice);

            datum.DecorCount = i;
            datum.DecorDirty = false;
        }

        private static float GridDecorHash01(int x, int y)
        {
            unchecked
            {
                var n = x * 374761393 + y * 668265263;
                n = (n ^ (n >> 13)) * 1274126177;
                return ((n ^ (n >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        private Box2 GetGridDecorAtlasRegion(int row, int seed)
        {
            var columns = _configuredGridDecorAtlasColumns;
            var rows = _configuredGridDecorAtlasRows;

            DebugTools.Assert(columns > 0);
            DebugTools.Assert(rows > 0);
            DebugTools.Assert(row >= 0 && row < rows);

            var variant = (int) ((uint) seed % (uint) columns);

            var cellW = 1f / columns;
            var cellH = 1f / rows;

            var left = variant * cellW;
            var right = left + cellW;

            var bottom = row * cellH;
            var top = bottom + cellH;

            return new Box2(left, bottom, right, top);
        }

        private static Vector2 GridDecorRandomPatchPosition(
            int patchX,
            int patchY,
            int patchSize,
            int seed)
        {
            var px =
                patchX +
                GridDecorHash01(seed * 17, seed * 31) * patchSize;

            var py =
                patchY +
                GridDecorHash01(seed * 53, seed * 11) * patchSize;

            return new Vector2(px, py);
        }

        private int WriteGridDecorTextureClumpToBuffers(
            int i,
            MapGridComponent grid,
            int patchX,
            int patchY,
            int patchSize,
            Span<Vertex2D> vertexBuffer,
            Span<ushort> indexBuffer,
            Box2 region,
            int clusterIndex)
        {
            var pos = GridDecorRandomPatchPosition(
                patchX,
                patchY,
                patchSize,
                patchX * 1000 + patchY * 100 + clusterIndex);

            if (!HasGridDecorAt(grid, pos.X, pos.Y))
                return i;

            var baseTileX = (int) MathF.Floor(pos.X);
            var baseTileY = (int) MathF.Floor(pos.Y);

            // Якщо пучок занадто близько до північної межі, а зверху вже не трав’яний тайл —
            // не малюємо його взагалі, а не стискаємо. Так не буде “вилазіння” і спотворень.
            if (pos.Y > baseTileY + 0.72f &&
                !HasGridDecorAt(grid, baseTileX + 0.5f, baseTileY + 1.05f))
            {
                return i;
            }

            var scaleSeed = GridDecorHash01(baseTileX + clusterIndex * 41, baseTileY - clusterIndex * 19);

            // Робимо PNG-пучок помітно більшим, бо сама текстура має прозорі поля.
            var width = MathHelper.Lerp(0.85f, 1.25f, scaleSeed);
            var height = MathHelper.Lerp(
                0.70f,
                1.05f,
                GridDecorHash01(baseTileX - 13, baseTileY + clusterIndex * 23));

            // Прив’язуємо низ пучка до землі. Текстура росте вгору.
            var bottom = pos.Y - 0.08f;
            var top = bottom + height;

            if (top > baseTileY + 0.98f &&
                !HasGridDecorAt(grid, baseTileX + 0.5f, baseTileY + 1.05f))
            {
                top = baseTileY + 0.98f;
            }

            var centerX = pos.X;
            var left = centerX - width * 0.5f;
            var right = centerX + width * 0.5f;

            // Не фарбуємо текстуру: використовуємо власний колір PNG,
            // лише трохи контролюючи прозорість.
            var color = Color.White.WithAlpha(0.88f);

            var vIdx = i * 4;

            const float UvEpsilon = 0.0015f;

            var uvBottom = region.Bottom + UvEpsilon;
            var uvTop = region.Top - UvEpsilon;

            vertexBuffer[vIdx + 0] = new Vertex2D(left, bottom, region.Left, uvBottom, color);
            vertexBuffer[vIdx + 1] = new Vertex2D(right, bottom, region.Right, uvBottom, color);
            vertexBuffer[vIdx + 2] = new Vertex2D(right, top, region.Right, uvTop, color);
            vertexBuffer[vIdx + 3] = new Vertex2D(left, top, region.Left, uvTop, color);

            var nIdx = i * GetQuadBatchIndexCount();
            var tIdx = (ushort) (i * 4);
            QuadBatchIndexWrite(indexBuffer, ref nIdx, tIdx);

            return i + 1;
        }
    }
}

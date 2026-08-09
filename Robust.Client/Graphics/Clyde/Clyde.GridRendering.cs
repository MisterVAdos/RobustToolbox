using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenToolkit.Graphics.OpenGL4;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using Robust.Shared.IoC;

namespace Robust.Client.Graphics.Clyde
{
    internal partial class Clyde
    {
        private readonly Dictionary<EntityUid, Dictionary<Vector2i, MapChunkData>> _mapChunkData =
            new();

        public void SetGridDecorWind(
            float power,
            float directionRadians,
            float gustPower)
        {
            _gridDecorWindPower = Math.Clamp(power, 0f, 1f);
            _gridDecorWindDirectionRadians = directionRadians;
            _gridDecorWindGustPower = Math.Clamp(gustPower, 0f, 1f);
        }

        /// <summary>
        /// To avoid spamming errors we'll just log it once and move on.
        /// </summary>
        private HashSet<Type> _erroredGridOverlays = new();

        private Vertex2D[]? _chunkMeshBuilderVertexBuffer;
        private ushort[]? _chunkMeshBuilderIndexBuffer;

        private int _verticesPerChunk(MapChunk chunk) => chunk.ChunkSize * chunk.ChunkSize * 4;
        private int _indicesPerChunk(MapChunk chunk) => chunk.ChunkSize * chunk.ChunkSize * GetQuadBatchIndexCount();

        private List<Entity<MapGridComponent>> _grids = new();
        private bool _drawTileEdges;

        // WH14 decor wind animation
        private float _whDecorTime;

        private float _gridDecorWindPower;
        private float _gridDecorWindDirectionRadians;
        private float _gridDecorWindGustPower;

        private Texture? _whDecorClumpAtlasTexture;

        private void RenderTileEdgesChanges(bool value)
        {
            _drawTileEdges = value;
            if (!value)
                return;

            // Dirty all Edges
            foreach (var gridData in _mapChunkData.Values)
            {
                foreach (var chunk in gridData.Values)
                {
                    chunk.EdgeDirty = true;
                }
            }
        }

        private void _drawGrids(Viewport viewport, Box2 worldAABB, Box2Rotated worldBounds, IEye eye)
        {
            var mapId = eye.Position.MapId;

            _whDecorTime += 1f / 60f;

// WH14: shader wind. Decor mesh більше не перебудовується заради вітру.
            var whDecorAnimate = false;

            if (!_mapManager.MapExists(mapId))
            {
                // fall back to nullspace map
                mapId = MapId.Nullspace;
            }

            _grids.Clear();
            _mapManager.FindGridsIntersecting(mapId, worldBounds, ref _grids);

            var requiresFlush = true;
            GLShaderProgram gridProgram = default!;
            var gridOverlays = GetOverlaysForSpace(OverlaySpace.WorldSpaceGrids);
            var mapSystem = _entityManager.System<SharedMapSystem>();

            foreach (var mapGrid in _grids)
            {
                if (!_mapChunkData.TryGetValue(mapGrid, out var data))
                {
                    continue;
                }

                if (requiresFlush)
                {
                    SetTexture(TextureUnit.Texture0, _tileDefinitionManager.TileTextureAtlas);
                    SetTexture(TextureUnit.Texture1, _lightingReady ? viewport.LightRenderTarget.Texture : _stockTextureWhite);
                    gridProgram = ActivateShaderInstance(_defaultShader.Handle).Item1;
                    SetupGlobalUniformsImmediate(gridProgram, (ClydeTexture) _tileDefinitionManager.TileTextureAtlas);

                    gridProgram.SetUniformTextureMaybe(UniIMainTexture, TextureUnit.Texture0);
                    gridProgram.SetUniformTextureMaybe(UniILightTexture, TextureUnit.Texture1);
                    gridProgram.SetUniform(UniIModUV, new Vector4(0, 0, 1, 1));
                }

                gridProgram.SetUniform(UniIModelMatrix, _transformSystem.GetWorldMatrix(mapGrid));
                var enumerator = mapSystem.GetMapChunks(mapGrid.Owner, mapGrid.Comp, worldBounds);

                // Handle base texture updates.
                while (enumerator.MoveNext(out var chunk))
                {
                    DebugTools.Assert(chunk.FilledTiles > 0);
                    var datum = EnsureChunkInitialized(data, chunk, mapGrid);

                    if (datum.Dirty) // WH14
                    {
                        _updateChunkMesh(mapGrid, chunk, datum);
                    }

                    if (datum.DecorDirty)
                    {
                        _updateChunkDecor(
                            mapGrid,
                            chunk,
                            datum);
                    }

                    if (!_drawTileEdges)
                        continue; // WH14-end

                    // Dirty edge tiles for next step.
                    datum.EdgeDirty = true;

                    for (var x = -1; x <= 1; x++)
                    {
                        for (var y = -1; y <= 1; y++)
                        {
                            var neighbor = chunk.Indices + new Vector2i(x, y);

                            if (!mapGrid.Comp.Chunks.TryGetValue(neighbor, out var neighborChunk))
                                continue;

                            var neighborDatum = EnsureChunkInitialized(data, neighborChunk, mapGrid);
                            neighborDatum.EdgeDirty = true;
                        }
                    }
                }

                // Handle edge sprites.
                if (_drawTileEdges)
                {
                    enumerator = mapSystem.GetMapChunks(mapGrid.Owner, mapGrid.Comp, worldBounds);
                    while (enumerator.MoveNext(out var chunk))
                    {
                        var datum = data[chunk.Indices];
                        if (datum.EdgeDirty)
                            _updateChunkEdges(mapGrid, chunk, datum);
                    }
                }

                enumerator = mapSystem.GetMapChunks(mapGrid.Owner, mapGrid.Comp, worldBounds);

                // Draw chunks
                while (enumerator.MoveNext(out var chunk))
                {
                    var datum = data[chunk.Indices];
                    DebugTools.Assert(datum.TileCount > 0);
                    if (datum.TileCount > 0)
                    {
                        BindVertexArray(datum.VAO);
                        CheckGlError();

                        _debugStats.LastGLDrawCalls += 1;
                        GL.DrawElements(GetQuadGLPrimitiveType(), datum.TileCount * GetQuadBatchIndexCount(), DrawElementsType.UnsignedShort, 0);
                        CheckGlError();
                    }

                    if (_drawTileEdges && datum.EdgeCount > 0)
                    {
                        BindVertexArray(datum.EdgeVAO);
                        CheckGlError();

                        _debugStats.LastGLDrawCalls += 1;
                        GL.DrawElements(GetQuadGLPrimitiveType(), datum.EdgeCount * GetQuadBatchIndexCount(), DrawElementsType.UnsignedShort, 0);
                        CheckGlError();
                    }

                    // WH14 Decor pass
                    if (datum.DecorCount > 0)
                    {
                        var whDecorTexture = WH14GetDecorClumpAtlasTexture();

                        SetTexture(TextureUnit.Texture0, whDecorTexture);
                        SetupGlobalUniformsImmediate(gridProgram, (ClydeTexture) whDecorTexture);
                        gridProgram.SetUniformTextureMaybe(UniIMainTexture, TextureUnit.Texture0);
                        gridProgram.SetUniformTextureMaybe(UniILightTexture, TextureUnit.Texture1);
                        gridProgram.SetUniform(UniIModUV, new Vector4(0, 0, 1, 1));

                        gridProgram.SetUniform("wh14DecorWindEnabled", 1f);
                        gridProgram.SetUniform("wh14WindTime", _whDecorTime);
                        gridProgram.SetUniform("wh14WindPower", _gridDecorWindPower);
                        gridProgram.SetUniform("wh14WindDirection", _gridDecorWindDirectionRadians);
                        gridProgram.SetUniform("wh14WindGust", _gridDecorWindGustPower);

                        BindVertexArray(datum.DecorVAO);
                        CheckGlError();

                        _debugStats.LastGLDrawCalls += 1;

                        GL.DrawElements(
                            GetQuadGLPrimitiveType(),
                            datum.DecorCount * GetQuadBatchIndexCount(),
                            DrawElementsType.UnsignedShort,
                            0);

                        CheckGlError();

                        gridProgram.SetUniform("wh14DecorWindEnabled", 0f);

                        SetTexture(TextureUnit.Texture0, _tileDefinitionManager.TileTextureAtlas);
                        SetupGlobalUniformsImmediate(gridProgram, (ClydeTexture) _tileDefinitionManager.TileTextureAtlas);
                        gridProgram.SetUniformTextureMaybe(UniIMainTexture, TextureUnit.Texture0);
                        gridProgram.SetUniformTextureMaybe(UniILightTexture, TextureUnit.Texture1);
                        gridProgram.SetUniform(UniIModUV, new Vector4(0, 0, 1, 1));
                    }
                }

                requiresFlush = false;

                foreach (var overlay in gridOverlays)
                {
                    if (overlay is not IGridOverlay iGrid)
                    {
                        if (!_erroredGridOverlays.Add(overlay.GetType()))
                        {
                            _clydeSawmill.Error($"Tried to render grid overlay {overlay.GetType()} that doesn't implement {nameof(IGridOverlay)}");
                        }

                        continue;
                    }

                    iGrid.Grid = mapGrid;
                    iGrid.RequiresFlush = false;
                    RenderSingleWorldOverlay(overlay, viewport, OverlaySpace.WorldSpaceGrids, worldAABB, worldBounds);
                    requiresFlush |= iGrid.RequiresFlush;
                }

                if (requiresFlush)
                {
                    FlushRenderQueue();
                }
            }

            CullEmptyChunks();
        }

        private MapChunkData EnsureChunkInitialized(Dictionary<Vector2i, MapChunkData> data, MapChunk chunk, Entity<MapGridComponent> mapGrid)
        {
            if (!data.TryGetValue(chunk.Indices, out var datum))
            {
                data[chunk.Indices] = datum = new MapChunkData();
                _initChunkBuffers(mapGrid, chunk, datum);
            }

            return datum;
        }

        private void CullEmptyChunks()
        {
            foreach (var (grid, chunks) in _mapChunkData)
            {
                var gridComp = _entityManager.GetComponent<MapGridComponent>(grid);
                foreach (var (index, chunk) in chunks)
                {
                    if (!chunk.Dirty || gridComp.Chunks.ContainsKey(index))
                    {
                        DebugTools.Assert(gridComp.Chunks[index].FilledTiles > 0);
                        continue;
                    }

                    DeleteChunk(chunk);
                    chunks.Remove(index);
                }
            }
        }

        private void _updateChunkMesh(Entity<MapGridComponent> grid, MapChunk chunk, MapChunkData datum)
        {
            Span<ushort> indexBuffer = EnsureSize(ref _chunkMeshBuilderIndexBuffer, _indicesPerChunk(chunk));
            Span<Vertex2D> vertexBuffer = EnsureSize(ref _chunkMeshBuilderVertexBuffer, _verticesPerChunk(chunk));

            var i = 0;
            var chunkSize = grid.Comp.ChunkSize;
            var chunkOriginScaled = chunk.Indices * chunkSize;

            for (ushort x = 0; x < chunkSize; x++)
            {
                for (ushort y = 0; y < chunkSize; y++)
                {
                    var gridX = x + chunkOriginScaled.X;
                    var gridY = y + chunkOriginScaled.Y;
                    var tile = chunk.GetTile(x, y);

                    // Tile render
                    if (x != chunkSize && y != chunkSize)
                    {
                        // ReSharper disable once IntVariableOverflowInUncheckedContext
                        if (tile.IsEmpty)
                            continue;

                        var regionMaybe = _tileDefinitionManager.TileAtlasRegion(tile);

                        Box2 region;
                        if (regionMaybe == null || regionMaybe.Length <= tile.Variant)
                        {
                            region = _tileDefinitionManager.ErrorTileRegion;
                        }
                        else
                        {
                            region = regionMaybe[tile.Variant];
                        }

                        var rotationMirroring = (_tileDefinitionManager.TryGetDefinition(tile.TypeId, out var tileDef) && tileDef.AllowRotationMirror) ?
                                                    tile.RotationMirroring
                                                    : 0;

                        WriteTileToBuffers(i, gridX, gridY, vertexBuffer, indexBuffer, region, rotationMirroring);
                        i += 1;
                    }
                }
            }

            var indexSlice = indexBuffer[..(i * GetQuadBatchIndexCount())];
            var vertSlice = vertexBuffer[..(i * 4)];

            GL.BindVertexArray(datum.VAO);
            CheckGlError();
            datum.EBO.Use();
            datum.VBO.Use();
            datum.EBO.Reallocate(indexSlice);
            datum.VBO.Reallocate(vertSlice);

            datum.TileCount = i;
            datum.Dirty = false;
        }

        private void _updateChunkEdges(Entity<MapGridComponent> grid, MapChunk chunk, MapChunkData datum)
        {
            // Need a buffer that can potentially store all neighbor tiles
            Span<ushort> indexBuffer = EnsureSize(ref _chunkMeshBuilderIndexBuffer, _indicesPerChunk(chunk) * 8);
            Span<Vertex2D> vertexBuffer = EnsureSize(ref _chunkMeshBuilderVertexBuffer, _verticesPerChunk(chunk) * 8);

            var i = 0;
            var chunkSize = grid.Comp.ChunkSize;
            var chunkOriginScaled = chunk.Indices * chunkSize;
            var maps = _entityManager.System<SharedMapSystem>();

            for (ushort x = 0; x < chunkSize; x++)
            {
                for (ushort y = 0; y < chunkSize; y++)
                {
                    var gridX = x + chunkOriginScaled.X;
                    var gridY = y + chunkOriginScaled.Y;
                    var tile = chunk.GetTile(x, y);
                    if (!_tileDefinitionManager.TryGetDefinition(tile.TypeId, out var tileDef))
                        continue;

                    // Edge render
                    for (var nx = -1; nx <= 1; nx++)
                    {
                        for (var ny = -1; ny <= 1; ny++)
                        {
                            if (nx == 0 && ny == 0)
                                continue;

                            var neighborIndices = new Vector2i(gridX + nx, gridY + ny);
                            if (!maps.TryGetTile(grid.Comp, neighborIndices, out var neighborTile))
                                continue;

                            if (!_tileDefinitionManager.TryGetDefinition(neighborTile.TypeId, out var neighborDef))
                                continue;

                            // If it's the same tile then no edge to be drawn.
                            if (tile.TypeId == neighborTile.TypeId || neighborDef.EdgeSprites.Count == 0)
                                continue;

                            // If neighbor is a lower or same priority then us then don't draw on our tile.
                            if (neighborDef.EdgeSpritePriority <= tileDef.EdgeSpritePriority)
                                continue;

                            var direction = new Vector2i(nx, ny).AsDirection().GetOpposite();
                            var regionMaybe = _tileDefinitionManager.TileAtlasRegion(neighborTile.TypeId, direction);

                            if (regionMaybe == null)
                                continue;

                            var region = regionMaybe[0];
                            WriteTileToBuffers(i, gridX, gridY, vertexBuffer, indexBuffer, region, 0);
                            i += 1;
                        }
                    }
                }
            }

            // We don't save the edge buffers back because we might need to re-use it if a neighbor chunk updates.
            var indexSlice = indexBuffer[..(i * GetQuadBatchIndexCount())];
            var vertSlice = vertexBuffer[..(i * 4)];

            GL.BindVertexArray(datum.EdgeVAO);
            CheckGlError();
            datum.EdgeEBO.Use();
            datum.EdgeVBO.Use();
            datum.EdgeEBO.Reallocate(indexSlice);
            datum.EdgeVBO.Reallocate(vertSlice);

            datum.EdgeCount = i;
            datum.EdgeDirty = false;
        }

        private bool WH14HasSurfaceDecor(Tile tile)
        {
            return WH14GetSurfaceDecor(tile) != null;
        }

        private string? WH14GetSurfaceDecor(Tile tile)
        {
            if (tile.IsEmpty)
                return null;

            if (!_tileDefinitionManager.TryGetDefinition(tile.TypeId, out var tileDef))
                return null;

            var type = tileDef.GetType();

            var field = type.GetField("WHSurfaceDecor");
            if (field?.GetValue(tileDef) is string fieldDecor && !string.IsNullOrWhiteSpace(fieldDecor))
                return fieldDecor;

            var prop = type.GetProperty("WHSurfaceDecor");
            if (prop?.GetValue(tileDef) is string propDecor && !string.IsNullOrWhiteSpace(propDecor))
                return propDecor;

            return null;
        }

        private bool WH14HasSurfaceDecorAt(MapGridComponent grid, float x, float y)
        {
            var maps = _entityManager.System<SharedMapSystem>();
            var tilePos = new Vector2i((int)MathF.Floor(x), (int)MathF.Floor(y));

            if (!maps.TryGetTile(grid, tilePos, out var tile))
                return false;

            return WH14HasSurfaceDecor(tile);
        }

        private Texture WH14GetDecorClumpAtlasTexture()
        {
            if (_whDecorClumpAtlasTexture != null)
                return _whDecorClumpAtlasTexture;

            var cache = IoCManager.Resolve<IResourceCache>();
            _whDecorClumpAtlasTexture = cache.GetResource<TextureResource>(
                "/Textures/_WH14/Planetary/Decor/grass_clumps_atlas.png").Texture;

            return _whDecorClumpAtlasTexture;
        }

        private void _updateChunkDecor( // WH14
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
                    var decor = WH14GetSurfaceDecor(tile);

                    if (decor == null)
                        continue;

                    var gridX = x + chunkOriginScaled.X;
                    var gridY = y + chunkOriginScaled.Y;

                    var patchSeed =
                        WH14Hash01(
                            chunk.Indices.X * 100 + x,
                            chunk.Indices.Y * 100 + y);

                    var clusterCount = 3 + (int)(patchSeed * 5f);

                    for (var cluster = 0; cluster < clusterCount; cluster++)
                    {
                        if ((i + 32) * 4 >= vertexBuffer.Length ||
                            (i + 32) * GetQuadBatchIndexCount() >= indexBuffer.Length)
                            break;

                        var region = WH14GetDecorAtlasRegion(
                            decor,
                            gridX * 92821 + gridY * 68917 + cluster * 193);

                        i = WH14WriteGrassTextureClumpToBuffers(
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

        private unsafe void _initChunkBuffers(Entity<MapGridComponent> grid, MapChunk chunk, MapChunkData datum)
        {
            var vboSize = _verticesPerChunk(chunk) * sizeof(Vertex2D);
            var eboSize = _indicesPerChunk(chunk) * sizeof(ushort);

            // Base VAO
            var vao = GenVertexArray();
            BindVertexArray(vao);
            CheckGlError();

            var vbo = new GLBuffer(this, BufferTarget.ArrayBuffer, BufferUsageHint.DynamicDraw,
                vboSize, $"Grid {grid.Owner} chunk {chunk.Indices} VBO");
            var ebo = new GLBuffer(this, BufferTarget.ElementArrayBuffer, BufferUsageHint.DynamicDraw,
                eboSize, $"Grid {grid.Owner} chunk {chunk.Indices} EBO");

            ObjectLabelMaybe(ObjectLabelIdentifier.VertexArray, vao, $"Grid {grid.Owner} chunk {chunk.Indices} VAO");
            SetupVAOLayout();
            CheckGlError();

            // Assign VBO and EBO to VAO.
            // OpenGL 3.x is such a good API.
            vbo.Use();
            ebo.Use();

            datum.EBO = ebo;
            datum.VBO = vbo;
            datum.VAO = vao;

            // EdgeVAO
            var edgeVao = GenVertexArray();
            BindVertexArray(edgeVao);
            CheckGlError();

            var edgeVbo = new GLBuffer(this, BufferTarget.ArrayBuffer, BufferUsageHint.DynamicDraw,
                vboSize * 8, $"Grid {grid.Owner} chunk {chunk.Indices} EdgeVBO");
            var edgeEbo = new GLBuffer(this, BufferTarget.ElementArrayBuffer, BufferUsageHint.DynamicDraw,
                eboSize * 8, $"Grid {grid.Owner} chunk {chunk.Indices} EdgeEBO");

            ObjectLabelMaybe(ObjectLabelIdentifier.VertexArray, vao, $"Grid {grid.Owner} chunk {chunk.Indices} EdgeVAO");
            SetupVAOLayout();
            CheckGlError();

            edgeVbo.Use();
            edgeEbo.Use();

            datum.EdgeEBO = edgeEbo;
            datum.EdgeVBO = edgeVbo;
            datum.EdgeVAO = edgeVao;

            // WH14 DecorVAO
            var decorVao = GenVertexArray();
            BindVertexArray(decorVao);
            CheckGlError();

            var decorVbo = new GLBuffer(this, BufferTarget.ArrayBuffer, BufferUsageHint.DynamicDraw,
                vboSize * 64, $"Grid {grid.Owner} chunk {chunk.Indices} WHDecorVBO");
            var decorEbo = new GLBuffer(this, BufferTarget.ElementArrayBuffer, BufferUsageHint.DynamicDraw,
                eboSize * 64, $"Grid {grid.Owner} chunk {chunk.Indices} WHDecorEBO");

            ObjectLabelMaybe(ObjectLabelIdentifier.VertexArray, decorVao, $"Grid {grid.Owner} chunk {chunk.Indices} WHDecorVAO");
            SetupVAOLayout();
            CheckGlError();

            decorVbo.Use();
            decorEbo.Use();

            datum.DecorEBO = decorEbo;
            datum.DecorVBO = decorVbo;
            datum.DecorVAO = decorVao;
        }

        private void DeleteChunk(MapChunkData data)
        {
            DeleteVertexArray(data.VAO);
            CheckGlError();
            data.VBO.Delete();
            data.EBO.Delete();

            DeleteVertexArray(data.EdgeVAO);
            CheckGlError();
            data.EdgeVBO.Delete();
            data.EdgeEBO.Delete();

            DeleteVertexArray(data.DecorVAO);
            CheckGlError();
            data.DecorVBO.Delete();
            data.DecorEBO.Delete();
        }

        private void _updateTileMapOnUpdate(ref TileChangedEvent args)
        {
            var gridData = _mapChunkData.GetOrNew(args.Entity);
            foreach (var change in args.Changes)
            {
                if (gridData.TryGetValue(change.ChunkIndex, out var data)) // WH14
                {
                    data.Dirty = true;
                    data.DecorDirty = true;
                }
            }
        }

        private void _updateOnGridCreated(GridStartupEvent ev)
        {
            var gridId = ev.EntityUid;
            _mapChunkData.GetOrNew(gridId);
        }

        private void _updateOnGridRemoved(GridRemovalEvent ev)
        {
            var gridId = ev.EntityUid;

            var data = _mapChunkData[gridId];
            foreach (var chunkDatum in data.Values)
            {
                DeleteChunk(chunkDatum);
            }

            _mapChunkData.Remove(gridId);
        }

        private static T[] EnsureSize<T>(ref T[]? field, int size)
        {
            if (field == null || field.Length < size)
                field = new T[size];

            return field;
        }

        private static float WH14Hash01(int x, int y)
        {
            unchecked
            {
                var n = x * 374761393 + y * 668265263;
                n = (n ^ (n >> 13)) * 1274126177;
                return ((n ^ (n >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        private void WriteTileToBuffers(
            int i,
            int gridX,
            int gridY,
            Span<Vertex2D> vertexBuffer,
            Span<ushort> indexBuffer,
            Box2 region,
            int rotationMirroring)
        {
            var rLeftBottom = (region.Left, region.Bottom);
            var rRightBottom = (region.Right, region.Bottom);
            var rRightTop = (region.Right, region.Top);
            var rLeftTop = (region.Left, region.Top);

            // The vertices must be changed if there's any rotation or mirroring to the tile
            if (rotationMirroring != 0)
            {
                // Rotate the tile
                for (int r = 0; r < rotationMirroring % 4; r++)
                {
                    (rLeftBottom, rRightBottom, rRightTop, rLeftTop) =
                        (rLeftTop, rLeftBottom, rRightBottom, rRightTop);
                }

                // Mirror on the x-axis
                if (rotationMirroring >= 4)
                {
                    if (rotationMirroring % 2 == 0)
                    {
                        rLeftBottom = (rLeftBottom.Item1.Equals(region.Left) ? region.Right : region.Left,
                            rLeftBottom.Item2);
                        rRightBottom = (rRightBottom.Item1.Equals(region.Left) ? region.Right : region.Left,
                            rRightBottom.Item2);
                        rRightTop = (rRightTop.Item1.Equals(region.Left) ? region.Right : region.Left,
                            rRightTop.Item2);
                        rLeftTop = (rLeftTop.Item1.Equals(region.Left) ? region.Right : region.Left,
                            rLeftTop.Item2);
                    }
                    else
                    {
                        rLeftBottom = (rLeftBottom.Item1,
                            rLeftBottom.Item2.Equals(region.Bottom) ? region.Top : region.Bottom);
                        rRightBottom = (rRightBottom.Item1,
                            rRightBottom.Item2.Equals(region.Bottom) ? region.Top : region.Bottom);
                        rRightTop = (rRightTop.Item1,
                            rRightTop.Item2.Equals(region.Bottom) ? region.Top : region.Bottom);
                        rLeftTop = (rLeftTop.Item1,
                            rLeftTop.Item2.Equals(region.Bottom) ? region.Top : region.Bottom);
                    }
                }
            }

            var vIdx = i * 4;
            vertexBuffer[vIdx + 0] = new Vertex2D(gridX, gridY, rLeftBottom.Left, rLeftBottom.Bottom, Color.White);
            vertexBuffer[vIdx + 1] = new Vertex2D(gridX + 1, gridY, rRightBottom.Right, rRightBottom.Bottom, Color.White);
            vertexBuffer[vIdx + 2] = new Vertex2D(gridX + 1, gridY + 1, rRightTop.Right, rRightTop.Top, Color.White);
            vertexBuffer[vIdx + 3] = new Vertex2D(gridX, gridY + 1, rLeftTop.Left, rLeftTop.Top, Color.White);
            var nIdx = i * GetQuadBatchIndexCount();
            var tIdx = (ushort)(i * 4);
            QuadBatchIndexWrite(indexBuffer, ref nIdx, tIdx);
        }

        private sealed class MapChunkData
        {
            public bool EdgeDirty = true;
            public bool Dirty = true;

            public uint VAO;
            public GLBuffer VBO = default!;
            public GLBuffer EBO = default!;
            public int TileCount;

            public uint EdgeVAO;
            public GLBuffer EdgeVBO = default!;
            public GLBuffer EdgeEBO = default!;
            public int EdgeCount;

            public bool DecorDirty = true; // WH14

            public uint DecorVAO;
            public GLBuffer DecorVBO = default!;
            public GLBuffer DecorEBO = default!;
            public int DecorCount; // WH14-end

            public MapChunkData()
            {
            }
        }

        private static Box2 WH14GetDecorAtlasRegion(string decor, int seed)
        {
            const int Columns = 3;
            const int Rows = 3;

            var variant = Math.Abs(seed) % Columns;

            var row = decor switch
            {
                "grass" => 0,

                "semi_drygrass" => 1,
                "semi_dry_grass" => 1,
                "semidrygrass" => 1,

                "drygrass" => 2,
                "dry_grass" => 2,

                _ => 0
            };

            var cellW = 1f / Columns;
            var cellH = 1f / Rows;

            var left = variant * cellW;
            var right = left + cellW;

            var bottom = row * cellH;
            var top = bottom + cellH;

            return new Box2(left, bottom, right, top);
        }

        private static Vector2 WH14RandomPatchPosition( // WH14
            int patchX,
            int patchY,
            int patchSize,
            int seed)
        {
            var px =
                patchX +
                WH14Hash01(seed * 17, seed * 31) * patchSize;

            var py =
                patchY +
                WH14Hash01(seed * 53, seed * 11) * patchSize;

            return new Vector2(px, py);
        }

private int WH14WriteGrassTextureClumpToBuffers(
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
    var pos = WH14RandomPatchPosition(
        patchX,
        patchY,
        patchSize,
        patchX * 1000 + patchY * 100 + clusterIndex);

    if (!WH14HasSurfaceDecorAt(grid, pos.X, pos.Y))
        return i;

    var baseTileX = (int)MathF.Floor(pos.X);
    var baseTileY = (int)MathF.Floor(pos.Y);

    // Якщо пучок занадто близько до північної межі, а зверху вже не трав’яний тайл —
    // не малюємо його взагалі, а не стискаємо. Так не буде “вилазіння” і спотворень.
    if (pos.Y > baseTileY + 0.72f &&
        !WH14HasSurfaceDecorAt(grid, baseTileX + 0.5f, baseTileY + 1.05f))
    {
        return i;
    }

    var scaleSeed = WH14Hash01(baseTileX + clusterIndex * 41, baseTileY - clusterIndex * 19);

    // Робимо PNG-пучок помітно більшим, бо сама текстура має прозорі поля.
    var width = MathHelper.Lerp(0.85f, 1.25f, scaleSeed);
    var height = MathHelper.Lerp(0.70f, 1.05f, WH14Hash01(baseTileX - 13, baseTileY + clusterIndex * 23));

    // Прив’язуємо низ пучка до землі. Текстура росте вгору.
    var bottom = pos.Y - 0.08f;
    var top = bottom + height;

    if (top > baseTileY + 0.98f &&
        !WH14HasSurfaceDecorAt(grid, baseTileX + 0.5f, baseTileY + 1.05f))
    {
        top = baseTileY + 0.98f;
    }

    var centerX = pos.X;
    var left = centerX - width * 0.5f;
    var right = centerX + width * 0.5f;

    // ВАЖЛИВО: не фарбуємо текстуру в зелений.
    // Даємо їй власний колір із PNG, тільки трохи контролюємо прозорість.
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
    var tIdx = (ushort)(i * 4);
    QuadBatchIndexWrite(indexBuffer, ref nIdx, tIdx);

    return i + 1;
}
    }
}

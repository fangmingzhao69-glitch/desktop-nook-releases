// Exact comparison of the current backdrop ROI with the ROI that was last
// presented. Out[0] = changed pixel count, Out[1] = max channel difference
// (0..255), Out[2 + tile] = per-tile max difference.

Texture2D<float4> Cur : register(t0);
Texture2D<float4> Prev : register(t1);
RWStructuredBuffer<uint> Out : register(u0);

cbuffer CompareParams : register(b0)
{
    uint Width;
    uint Height;
    uint TilesX;
    uint TileSize;
};

groupshared uint GroupCount;
groupshared uint GroupMax;

[numthreads(16, 16, 1)] void CS(uint3 id : SV_DispatchThreadID, uint3 local : SV_GroupThreadID, uint3 group : SV_GroupID) {
    if (local.x == 0 && local.y == 0) {
        GroupCount = 0;
        GroupMax = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    uint m = 0;
    if (id.x < Width && id.y < Height) {
        float3 d = abs(Cur.Load(int3(id.xy, 0)).rgb - Prev.Load(int3(id.xy, 0)).rgb);
        m = (uint)round(max(d.r, max(d.g, d.b)) * 255.0);
    }
    if (m != 0) {
        InterlockedAdd(GroupCount, 1);
        InterlockedMax(GroupMax, m);
        // The production renderer uses one tile; the heatmap fixture uses 16 px tiles.
        // Preserve arbitrary tile sizes if a future caller crosses a tile in one group.
        if (TileSize != 16 && TileSize < max(Width, Height))
            InterlockedMax(Out[2 + (id.y / TileSize) * TilesX + id.x / TileSize], m);
    }
    GroupMemoryBarrierWithGroupSync();
    if (local.x == 0 && local.y == 0 && GroupCount != 0) {
        InterlockedAdd(Out[0], GroupCount);
        InterlockedMax(Out[1], GroupMax);
        if (TileSize == 16 || TileSize >= max(Width, Height))
            InterlockedMax(Out[2 + (group.y * 16 / TileSize) * TilesX + group.x * 16 / TileSize], GroupMax);
    }
}

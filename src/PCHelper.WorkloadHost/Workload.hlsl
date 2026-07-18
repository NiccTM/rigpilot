RWStructuredBuffer<float4> Data : register(u0);

[numthreads(256, 1, 1)]
void CoreMain(uint3 id : SV_DispatchThreadID)
{
    uint count;
    uint stride;
    Data.GetDimensions(count, stride);
    if (id.x >= min(count, 1048576u)) return;

    float4 value = Data[id.x];
    value += float4(0.0001f, 0.0002f, 0.0003f, 0.0004f) * (id.x + 1u);
    [loop]
    for (uint i = 0; i < 96; ++i)
    {
        value = mad(value.yzwx, 1.000001f, value * 0.000003f + 0.000001f);
        value = frac(abs(value));
    }
    Data[id.x] = value;
}

[numthreads(256, 1, 1)]
void MemoryMain(uint3 id : SV_DispatchThreadID)
{
    uint count;
    uint stride;
    Data.GetDimensions(count, stride);
    const uint rowWidth = 65535u * 256u;
    uint index = id.x + (id.y * rowWidth);
    if (index >= count) return;

    uint peer = (index + 4093u) % count;
    float4 value = Data[peer];
    Data[index] = value.wxyz + float4(0.000001f, 0.000002f, 0.000003f, 0.000004f);
}

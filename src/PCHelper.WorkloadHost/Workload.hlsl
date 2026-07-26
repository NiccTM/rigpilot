RWStructuredBuffer<float4> Data : register(u0);

// --- Artifact detection ------------------------------------------------------------------
// Screening can measure throughput and observe a driver reset, but neither sees the failure
// mode that matters most for memory overclocking: silently WRONG results. GDDR6X carries
// on-die error correction, so an unstable module does not fault — it corrects, slows, and
// corrupts what is on screen. A compute workload that only accumulates numbers cannot tell
// a corrupted value from a legitimate one, because every result is as plausible as any other.
//
// So the artifact pass computes a value that depends ONLY on the element index, and checks
// it. ArtifactSeed writes f(index); ArtifactVerify recomputes f(index) and compares. Any
// difference is a bit that did not survive the round trip through memory, which is precisely
// the corruption a user sees as on-screen glitching.
//
// The pattern is chosen to be exactly representable in binary32 — a 16-bit integer widened
// to float — so the comparison is exact. No epsilon, no false positives from rounding: a
// mismatch is a real corrupted bit, and the verify counts it atomically.
RWStructuredBuffer<float4> Artifact : register(u1);
RWStructuredBuffer<uint> ArtifactErrors : register(u2);

float4 ExpectedPattern(uint index)
{
    // Masked to 16 bits so every component is an integer below 65536 and therefore exact in
    // a 32-bit float; the offsets keep the four components distinct.
    float base = (float)(index & 0xFFFFu);
    return float4(base, base + 1.0f, base + 2.0f, base + 3.0f);
}

[numthreads(256, 1, 1)]
void ArtifactSeedMain(uint3 id : SV_DispatchThreadID)
{
    uint count;
    uint stride;
    Artifact.GetDimensions(count, stride);
    if (id.x >= count) return;
    Artifact[id.x] = ExpectedPattern(id.x);
}

[numthreads(256, 1, 1)]
void ArtifactVerifyMain(uint3 id : SV_DispatchThreadID)
{
    uint count;
    uint stride;
    Artifact.GetDimensions(count, stride);
    if (id.x >= count) return;

    float4 expected = ExpectedPattern(id.x);
    float4 actual = Artifact[id.x];
    // Exact comparison: the pattern is integral and exactly representable, so any difference
    // is corruption rather than arithmetic error.
    if (any(actual != expected))
    {
        uint previous;
        InterlockedAdd(ArtifactErrors[0], 1u, previous);
    }
}

[numthreads(256, 1, 1)]
void CoreMain(uint3 id : SV_DispatchThreadID)
{
    uint count;
    uint stride;
    Data.GetDimensions(count, stride);
    if (id.x >= min(count, 1048576u)) return;

    float4 value = Data[id.x];
    value += float4(0.0001f, 0.0002f, 0.0003f, 0.0004f) * (id.x + 1u);
    // Each dispatch must carry enough arithmetic that the queued work outlasts
    // the host's completion wait (~1-15 ms of Windows timer granularity). At 96
    // iterations a dispatch finished long before that, so the device drained and
    // idled and utilisation sat near a third of the card. This works only in
    // combination with the in-flight batching in Program.cs — raising the count
    // alone measured 38.1%, and both together measured 100%. A dispatch remains
    // milliseconds long, far below the driver's TDR budget.
    [loop]
    for (uint i = 0; i < 2048; ++i)
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

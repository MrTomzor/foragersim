/// <summary>
/// Deterministic integer seed mixing for world generation.
///
/// <c>System.HashCode.Combine</c> must NOT be used to derive RNG seeds: it is seeded with a
/// random value per process / AppDomain, so the same world seed produced a different
/// layout after every Unity restart, Editor domain reload or recompile (measured
/// 2026-09-03 with WorldGenDump: the `default` layout and the memory maze changed
/// between play sessions at seed 42). This is a fixed-constant mixer (murmur3
/// finaliser over the two inputs) with the property XOR lacks — adjacent seeds and
/// attempt indices do not collide (`seed ^ attempt` maps (43,0) and (42,1) to the
/// same value) — and it is the same in every process.
/// </summary>
public static class SeedMix {
    public static int Combine(int a, int b) {
        unchecked {
            uint h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b + 0x7F4A7C15u + (h << 6) + (h >> 2);
            h ^= h >> 16; h *= 0x85EBCA6Bu;
            h ^= h >> 13; h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return (int)h;
        }
    }

    public static int Combine(int a, int b, int c) => Combine(Combine(a, b), c);

    /// <summary>FNV-1a over UTF-16 code units — a string hash that is the same in every runtime.</summary>
    public static int StableStringHash(string s) {
        unchecked {
            uint h = 2166136261u;
            if (s != null)
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return (int)h;
        }
    }
}

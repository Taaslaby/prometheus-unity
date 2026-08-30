// RNG-паритет с Rust: ChaCha12 (StdRng из rand 0.8) + XorShift (dioma rng.rs).
//
// StdRng = ChaCha12Rng: 32-байтный сид из PCG32 (rand_core::seed_from_u64),
// nonce 8 нулевых байт, буфер 64 u32 (4 чанкa-блока по 16 слов), выдача LE.
// f64: 53 старших бита u64 × 2^-53; f32: 24 старших бита u32 × 2^-24.
// gen_range(low..high): мантисса 52 бита (u64 >> 12) в [1,2) − 1, × scale + low,
// scale = high−low, при res >= high — цикл уменьшения scale на 1 ulp.

using System;

namespace Prometheus.Runtime
{
    /// ChaCha12 — точный порт rand_chacha 0.3.1 StdRng.
    public sealed class ChaCha12
    {
        const int Rounds = 12; // ChaCha12 (StdRng)
        uint[] _key = new uint[8];      // state b,c (32 байта сида)
        uint[] _ctrNonce = new uint[4]; // state d
        uint[] _buf = new uint[64];     // 4 блока × 16 слов
        int _idx = 64;                   // пуст → refill при первом use

        static readonly uint[] Sigma =
            { 0x61707865, 0x3320646e, 0x79622d32, 0x6b206574 };

        public ChaCha12(ulong seed) : this(SeedFromU64(seed)) { }

        public ChaCha12(byte[] seed32)
        {
            if (seed32 == null) return; // for CloneState
            if (seed32.Length != 32) throw new ArgumentException("seed must be 32 bytes");
            for (int i = 0; i < 8; i++)
                _key[i] = BitConverter.ToUInt32(seed32, i * 4);
            // nonce = [0u8; 8] → ctr_nonce = [0, 0, 0, 0]
        }

        static byte[] SeedFromU64(ulong state)
        {
            // rand_core 0.6.4 SeedableRng::seed_from_u64 — PCG32 × 8 слов.
            const ulong Mul = 6364136223846793005;
            const ulong Inc = 11634580027462260723;
            var seed = new byte[32];
            for (int chunk = 0; chunk < 8; chunk++)
            {
                state = unchecked(state * Mul + Inc);
                ulong s = state;
                uint xorshifted = (uint)(((s >> 18) ^ s) >> 27);
                uint rot = (uint)(s >> 59);
                uint x = RotateRight(xorshifted, (int)rot);
                seed[chunk * 4 + 0] = (byte)x;
                seed[chunk * 4 + 1] = (byte)(x >> 8);
                seed[chunk * 4 + 2] = (byte)(x >> 16);
                seed[chunk * 4 + 3] = (byte)(x >> 24);
            }
            return seed;
        }

        static uint RotateRight(uint v, int r) => (uint)((v >> r) | (v << (32 - r)));

        void Refill()
        {
            // Буфер = 4 блока со счётчиками pos..pos+3 (первый refill: 0..3,
            // свято по rand_chacha: state.d после первого refill = 4).
            ulong pos = ((ulong)_ctrNonce[1] << 32) | _ctrNonce[0];
            int w = 0;
            for (int block = 0; block < 4; block++)
            {
                ulong ctr = unchecked(pos + (ulong)block);
                Block((uint)ctr, (uint)(ctr >> 32), _ctrNonce[2], _ctrNonce[3], _buf, w);
                w += 16;
            }
            ulong next = unchecked(pos + 4);
            _ctrNonce[0] = (uint)next;
            _ctrNonce[1] = (uint)(next >> 32);
            _idx = 0;
        }

        void Block(uint ctrLo, uint ctrHi, uint nonce0, uint nonce1, uint[] outBuf, int off)
        {
            uint[] x = new uint[16];
            x[0] = Sigma[0]; x[1] = Sigma[1]; x[2] = Sigma[2]; x[3] = Sigma[3];
            x[4] = _key[0]; x[5] = _key[1]; x[6] = _key[2]; x[7] = _key[3];
            x[8] = _key[4]; x[9] = _key[5]; x[10] = _key[6]; x[11] = _key[7];
            x[12] = ctrLo; x[13] = ctrHi; x[14] = nonce0; x[15] = nonce1;

            for (int r = 0; r < Rounds; r += 2)
            {
                // Column rounds
                Quarter(ref x[0], ref x[4], ref x[8], ref x[12]);
                Quarter(ref x[1], ref x[5], ref x[9], ref x[13]);
                Quarter(ref x[2], ref x[6], ref x[10], ref x[14]);
                Quarter(ref x[3], ref x[7], ref x[11], ref x[15]);
                // Diagonal rounds
                Quarter(ref x[0], ref x[5], ref x[10], ref x[15]);
                Quarter(ref x[1], ref x[6], ref x[11], ref x[12]);
                Quarter(ref x[2], ref x[7], ref x[8], ref x[13]);
                Quarter(ref x[3], ref x[4], ref x[9], ref x[14]);
            }

            for (int i = 0; i < 16; i++)
                outBuf[off + i] = x[i] + (i < 4 ? Sigma[i] : (i < 12 ? _key[i - 4] : CtrWord(i - 12, ctrLo, ctrHi, nonce0, nonce1)));
        }

        static uint CtrWord(int i, uint ctrLo, uint ctrHi, uint n0, uint n1) =>
            i == 0 ? ctrLo : i == 1 ? ctrHi : i == 2 ? n0 : n1;

        static void Quarter(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a += b; d ^= a; d = (d << 16) | (d >> 16);
            c += d; b ^= c; b = (b << 12) | (b >> 20);
            a += b; d ^= a; d = (d << 8) | (d >> 24);
            c += d; b ^= c; b = (b << 7) | (b >> 25);
        }

        public uint NextU32()
        {
            if (_idx >= 64) Refill();
            return _buf[_idx++];
        }

        public ulong NextU64()
        {
            // BlockRng::next_u64: пара u32 из буфера (LE), возможно через границу.
            if (_idx < 64 - 1)
            {
                ulong lo = _buf[_idx];
                ulong hi = _buf[_idx + 1];
                _idx += 2;
                return (hi << 32) | lo;
            }
            if (_idx >= 64)
            {
                Refill();
                ulong lo = _buf[0];
                ulong hi = _buf[1];
                _idx = 2;
                return (hi << 32) | lo;
            }
            // нечётный хвост: старое слово [63] — младшие, новое [0] — старшие
            ulong x = _buf[63];
            Refill();
            ulong y = _buf[0];
            _idx = 1;
            return (y << 32) | x;
        }

        /// Standard [0,1): 53 старших бита.
        public double GenF64()
        {
            ulong v = NextU64();
            ulong bits = v >> 11; // 53 бита
            return bits * (1.0 / (1UL << 53));
        }

        /// Standard [0,1) f32: 24 старших бита.
        public float GenF32()
        {
            uint v = NextU32();
            uint bits = v >> 8; // 24 бита
            return bits * (1f / (1u << 24));
        }

        /// gen_range(min..max) — UniformFloat::sample (scale-loop семантика).
        public double GenRange(double low, double high)
        {
            double scale = high - low;
            while (true)
            {
                // u64 >> 12 → [1,2) через экспоненту 0, − 1 → [0,1)
                ulong raw = NextU64() >> 12;
                ulong withExp = raw | (1023UL << 52);
                double value01 = BitConverter.Int64BitsToDouble((long)withExp) - 1.0;
                double res = value01 * scale + low;
                if (res < high) return res;
                // edge-case уменьшение scale (как в rand 0.8.6)
                scale = PrevDouble(scale);
            }
        }

        static double PrevDouble(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            if (bits > 0) return BitConverter.Int64BitsToDouble(bits - 1);
            if (bits == long.MinValue) return bits == 0 ? d : BitConverter.Int64BitsToDouble(bits - 1);
            return BitConverter.Int64BitsToDouble(bits + (bits > 0 ? -1 : 1));
        }

        public ChaCha12 CloneState()
        {
            var c = new ChaCha12((byte[])null)
            {
                _idx = _idx,
                _buf = (uint[])_buf.Clone(),
                _key = (uint[])_key.Clone(),
                _ctrNonce = (uint[])_ctrNonce.Clone(),
            };
            return c;
        }
    }

    /// XorShift — порт dioma rng.rs (расстановка сом в grow).
    public struct XorShift
    {
        public ulong S;

        public XorShift(ulong seed) { S = seed; }

        public ulong NextU64()
        {
            ulong x = S;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            S = x;
            return x;
        }

        public double Unit() => (NextU64() >> 11) / (double)(1UL << 53);

        /// Сумма трёх равномерных ≈ гауссиана (мутации Steering).
        public double Gauss() => Unit() + Unit() + Unit() - 1.5;
    }
}

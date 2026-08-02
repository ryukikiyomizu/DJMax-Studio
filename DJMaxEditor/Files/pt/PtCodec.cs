using System;

namespace DJMaxEditor.Files.pt
{
    /// <summary>
    /// Offline, in-process encrypt/decrypt for encrypted Technika / Trilogy <c>.pt</c> charts.
    ///
    /// This is a faithful managed port of @wchdsk's "Djmax PT enc&amp;dec v0.2" (pt_tool / pt_dec.c),
    /// used here with the author's explicit permission to port/reuse it for the DJMax Editor.
    /// It replaces the dead UnpackMe online service: no network, no external process, no temp files.
    ///
    /// Algorithm (reproduced exactly): a 24-byte plaintext header seeds an MT19937 generator via
    /// init-by-array; a per-8-byte-block XOR keystream is formed from (a) MT19937 tempered outputs and
    /// (b) a 32-round XTEA state advance whose key material is the running plaintext block (a
    /// plaintext-feedback chaining). Mode is auto-detected from the first body u32: &lt;= 10 =&gt; the file
    /// is already decrypted (encrypt), otherwise decrypt — matching pt_tool and
    /// <see cref="FormatDetection.ChartFormatDetector"/>.
    ///
    /// The codec is deterministic and self-contained: all state is per-instance, so repeated calls
    /// produce identical output and calls are independent.
    /// </summary>
    public sealed class PtCodec
    {
        public const int HeaderSize = 0x18;      // 24 bytes of plaintext header
        public const byte ModeDecode = 0;        // encrypted -> decrypted
        public const byte ModeEncode = 1;        // decrypted -> encrypted

        private const uint MatrixA = 0x9908B0DFu; // MT19937 (== KEY_BLOB_2[1])
        private const uint TeaDelta = 0x9E3779B9u; // XTEA delta; C uses `-= 1640531527`

        // Standard CRC-32 (IEEE) table == pt_tool's KEY_BLOB_1. Generated once, verified table[1]==0x77073096.
        private static readonly uint[] Crc32 = BuildCrc32Table();

        // ---- per-conversion state (mirrors pt_dec.c globals / keydata fields) ----
        private readonly byte[] _header = new byte[HeaderSize];
        private readonly uint[] _key1 = new uint[2];        // C global key_1 (running XTEA state)
        private readonly byte[] _kdKey1 = new byte[8];      // key_data.key_1 (8 bytes; plaintext feedback + uint view)
        private readonly byte[] _kdUnknown1 = new byte[8];  // key_data.unknown_1
        private readonly uint[] _blob = new uint[626];      // key_data.key_data_blob; _blob[624] == MT index
        private readonly uint[] _kdKey2 = new uint[2];      // key_data.key_2
        private readonly byte[] _tempKey1 = new byte[8];    // temp_key_1 (LE bytes of key_1)
        private readonly byte[] _tempKey2 = new byte[8];    // temp_key_2 (LE bytes of key_2)
        private byte _mode;
        private byte[] _body;                               // pt payload from 0x18 onward

        /// <summary>Result of a conversion: the detected direction and the transformed bytes.</summary>
        public struct Result
        {
            public byte Mode;      // ModeDecode or ModeEncode (what was actually performed)
            public byte[] Data;    // full file bytes (header + transformed body)
        }

        /// <summary>
        /// Auto-detecting convert. If the body's first u32 &gt; 10 the file is decrypted (encrypted output
        /// is returned with <c>Mode == ModeDecode</c>); otherwise it is encrypted. Never mutates <paramref name="pt"/>.
        /// </summary>
        public static Result Convert(byte[] pt)
        {
            if (pt == null) throw new ArgumentNullException(nameof(pt));
            if (pt.Length < HeaderSize + 4)
                throw new ArgumentException("File is too short to contain a PT header and body.", nameof(pt));
            return new PtCodec().Run(pt);
        }

        /// <summary>Decrypt an encrypted Technika/Trilogy chart. Throws if the input was already decrypted.</summary>
        public static byte[] Decrypt(byte[] encrypted)
        {
            var r = Convert(encrypted);
            if (r.Mode != ModeDecode)
                throw new InvalidOperationException("Input was not an encrypted PT (first body word <= 10).");
            return r.Data;
        }

        /// <summary>Encrypt a decrypted chart back to the on-disk encrypted form.</summary>
        public static byte[] Encrypt(byte[] decrypted)
        {
            var r = Convert(decrypted);
            if (r.Mode != ModeEncode)
                throw new InvalidOperationException("Input was not a decrypted PT (first body word > 10).");
            return r.Data;
        }

        private Result Run(byte[] pt)
        {
            var outData = (byte[])pt.Clone();
            Buffer.BlockCopy(outData, 0, _header, 0, HeaderSize);

            int bodyLen = outData.Length - HeaderSize;
            _body = new byte[bodyLen];
            Buffer.BlockCopy(outData, HeaderSize, _body, 0, bodyLen);

            uint decFlag = U32(_body, 0);
            _mode = decFlag <= 10 ? ModeEncode : ModeDecode;

            FillData();

            // key_data.key_1[1] = CRC32(header); [0] = byteSum(header)
            W32(_kdKey1, 4, GetParam1(_header));
            W32(_kdKey1, 0, GetParam2(_header));
            _key1[0] = U32(_kdKey1, 0);
            _key1[1] = U32(_kdKey1, 4);
            W32(_tempKey1, 0, _key1[0]);
            W32(_tempKey1, 4, _key1[1]);

            _kdKey2[0] = CalcParam2();
            _kdKey2[1] = CalcParam2();
            W32(_tempKey2, 0, _kdKey2[0]);
            W32(_tempKey2, 4, _kdKey2[1]);

            Encrypt();

            Buffer.BlockCopy(_header, 0, outData, 0, HeaderSize);
            Buffer.BlockCopy(_body, 0, outData, HeaderSize, bodyLen);
            return new Result { Mode = _mode, Data = outData };
        }

        // The 8-byte-block XOR loop with plaintext feedback (encrypt() in pt_dec.c).
        private void Encrypt()
        {
            int y = 0;
            for (int x = 0; x < _body.Length; x++)
            {
                if (_mode == ModeEncode)
                {
                    _kdKey1[y] = _body[x];      // feed pre-XOR plaintext
                    _kdUnknown1[y] = _body[x];
                }

                _body[x] ^= (byte)(_tempKey2[y] ^ _tempKey1[y]);

                if (_mode == ModeDecode)
                {
                    _kdKey1[y] = _body[x];      // feed post-XOR plaintext
                    _kdUnknown1[y] = _body[x];
                }

                if (++y == 8)
                {
                    // unknown_1 = key_1 (kd); both already hold the plaintext block — kept for fidelity.
                    Buffer.BlockCopy(_kdKey1, 0, _kdUnknown1, 0, 8);
                    UpdateParam();
                    _kdKey2[0] = CalcParam2();
                    _kdKey2[1] = CalcParam2();
                    W32(_tempKey2, 0, _kdKey2[0]);
                    W32(_tempKey2, 4, _kdKey2[1]);
                    y = 0;
                }
            }
        }

        // 32-round XTEA advance of the running state (update_param in pt_dec.c). unchecked = C unsigned wrap.
        private void UpdateParam()
        {
            unchecked
            {
                uint v8 = _key1[0];
                uint v5 = _key1[1];
                uint sum = 0;
                uint k0 = U32(_kdKey1, 0), k1 = U32(_kdKey1, 4);
                uint u0 = U32(_kdUnknown1, 0), u1 = U32(_kdUnknown1, 4);
                for (int r = 0; r < 32; r++)
                {
                    sum -= 1640531527u; // == sum += TeaDelta
                    v8 += (k1 + (v5 >> 5)) ^ (sum + v5) ^ (k0 + 16 * v5);
                    v5 += (u1 + (v8 >> 5)) ^ (sum + v8) ^ (u0 + 16 * v8);
                }
                _key1[0] = v8;
                _key1[1] = v5;
                W32(_kdKey1, 0, v8);
                W32(_kdKey1, 4, v5);
                W32(_tempKey1, 0, v8);
                W32(_tempKey1, 4, v5);
            }
        }

        // MT19937 tempered output (calc_param_2 in pt_dec.c). _blob[624] is the MT index (mti).
        private uint CalcParam2()
        {
            unchecked
            {
                if (_blob[624] >= 624)
                {
                    int i;
                    for (i = 0; i < 227; i++)
                    {
                        uint v1 = (_blob[i + 1] & 0x7FFFFFFF) | (_blob[i] & 0x80000000);
                        _blob[i] = MagOf(v1) ^ _blob[i + 397] ^ (v1 >> 1);
                    }
                    for (; i < 623; i++)
                    {
                        uint v2 = (_blob[i + 1] & 0x7FFFFFFF) | (_blob[i] & 0x80000000);
                        _blob[i] = MagOf(v2) ^ _blob[i - 227] ^ (v2 >> 1);
                    }
                    uint v3 = (_blob[0] & 0x7FFFFFFF) | (_blob[623] & 0x80000000);
                    _blob[623] = MagOf(v3) ^ _blob[396] ^ (v3 >> 1);
                    _blob[624] = 0;
                }

                uint y = _blob[_blob[624]];
                _blob[624]++;
                y ^= y >> 11;
                y ^= (y << 7) & 0x9D2C5680;
                y ^= (y << 15) & 0xEFC60000;
                y ^= y >> 18;
                return y;
            }
        }

        // MT19937 init-by-array on the 24-byte header (fill_data in pt_dec.c).
        private void FillData()
        {
            unchecked
            {
                FillKeyDataBlob();
                int v9 = 1, v6 = 0;
                const int a3 = 6; // header = 6 u32 words
                for (int i = 624; i != 0; i--)
                {
                    uint kw = U32(_header, 4 * v6);
                    _blob[v9] = (uint)v6 + kw + (_blob[v9] ^ (1664525u * (_blob[v9 - 1] ^ (_blob[v9 - 1] >> 30))));
                    v9++; v6++;
                    if (v9 >= 624) { _blob[0] = _blob[623]; v9 = 1; }
                    if (v6 >= a3) v6 = 0;
                }
                for (int j = 623; j != 0; j--)
                {
                    _blob[v9] = (_blob[v9] ^ (1566083941u * (_blob[v9 - 1] ^ (_blob[v9 - 1] >> 30)))) - (uint)v9;
                    v9++;
                    if (v9 >= 624) { _blob[0] = _blob[623]; v9 = 1; }
                }
                _blob[0] = 0x80000000u;
            }
        }

        // MT19937 seeding with the fixed constant seed 0x12BD6AA (fill_key_data_blob in pt_dec.c).
        private void FillKeyDataBlob()
        {
            unchecked
            {
                _blob[0] = 0x12BD6AAu;
                for (uint idx = 1; idx < 624; idx++)
                    _blob[idx] = idx + 1812433253u * (_blob[idx - 1] ^ (_blob[idx - 1] >> 30));
                _blob[624] = 624; // matches C loop's exit state, forces a twist on first CalcParam2
            }
        }

        // CRC-32 over the header (get_param_1 in pt_dec.c).
        private static uint GetParam1(byte[] header)
        {
            unchecked
            {
                uint v4 = 0xFFFFFFFFu;
                for (int i = 0; i < header.Length; i++)
                {
                    uint add = (v4 & 0xFF) ^ header[i];
                    v4 = Crc32[add] ^ (v4 >> 8);
                }
                return ~v4;
            }
        }

        // Byte sum over the header (get_param_2 in pt_dec.c).
        private static uint GetParam2(byte[] header)
        {
            unchecked
            {
                uint sum = 0;
                for (int i = 0; i < header.Length; i++) sum += header[i];
                return sum;
            }
        }

        private static uint MagOf(uint v) { return (v & 1) == 0 ? 0u : MatrixA; }

        private static uint U32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        private static void W32(byte[] b, int off, uint v)
        {
            b[off] = (byte)v;
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
        }

        private static uint[] BuildCrc32Table()
        {
            var t = new uint[256];
            unchecked
            {
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
            }
            return t;
        }
    }
}

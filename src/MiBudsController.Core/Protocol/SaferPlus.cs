using System.Security.Cryptography;

namespace MiBudsController.Core.Protocol;

/// <summary>
/// 小米耳机使用的自定义蓝牙 SAFER+ 变体（128 位密钥、8 轮加密），
/// 移植自 Gadgetbridge 的 Authentication.java / AuthData.java。
/// </summary>
public sealed class SaferPlus
{
    private const int Pattern = 0x9999;
    private const int BlockSize = 16;

    private static readonly byte[] Sequence =
    {
        0x11, 0x22, 0x33, 0x33, 0x22, 0x11, 0x11, 0x22,
        0x33, 0x33, 0x22, 0x11, 0x11, 0x22, 0x33, 0x33,
    };

    private static readonly int[][] Coefficients =
    {
        new[] { 2, 1, 1, 1, 4, 2, 1, 1, 2, 2, 4, 2, 4, 4, 16, 8 },
        new[] { 2, 1, 1, 1, 4, 2, 1, 1, 1, 1, 2, 1, 2, 2, 8, 4 },
        new[] { 1, 1, 4, 2, 2, 2, 4, 2, 16, 8, 4, 4, 2, 1, 1, 1 },
        new[] { 1, 1, 4, 2, 1, 1, 2, 1, 8, 4, 2, 2, 2, 1, 1, 1 },
        new[] { 16, 8, 2, 2, 4, 2, 4, 4, 1, 1, 4, 2, 1, 1, 2, 1 },
        new[] { 8, 4, 1, 1, 2, 1, 2, 2, 1, 1, 4, 2, 1, 1, 2, 1 },
        new[] { 2, 2, 4, 2, 4, 4, 16, 8, 2, 1, 1, 1, 4, 2, 1, 1 },
        new[] { 1, 1, 2, 1, 2, 2, 8, 4, 2, 1, 1, 1, 4, 2, 1, 1 },
        new[] { 4, 2, 4, 4, 16, 8, 2, 2, 1, 1, 2, 1, 1, 1, 4, 2 },
        new[] { 2, 1, 2, 2, 8, 4, 1, 1, 1, 1, 2, 1, 1, 1, 4, 2 },
        new[] { 4, 4, 16, 8, 1, 1, 2, 1, 4, 2, 1, 1, 4, 2, 2, 2 },
        new[] { 2, 2, 8, 4, 1, 1, 2, 1, 4, 2, 1, 1, 2, 1, 1, 1 },
        new[] { 1, 1, 2, 1, 1, 1, 4, 2, 4, 4, 16, 8, 2, 2, 4, 2 },
        new[] { 1, 1, 2, 1, 1, 1, 4, 2, 2, 2, 8, 4, 1, 1, 2, 1 },
        new[] { 4, 2, 1, 1, 2, 1, 1, 1, 4, 2, 2, 2, 16, 8, 4, 4 },
        new[] { 4, 2, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 8, 4, 2, 2 },
    };

    private readonly byte[][] _biasMatrix = new byte[16][];
    private readonly byte[] _expTab = new byte[256];
    private readonly byte[] _logTab = new byte[256];

    /// <summary>构造时生成偏置矩阵、指数表和对数表，供加解密使用。</summary>
    public SaferPlus()
    {
        GenerateBiasMatrix();
        GenerateExpTab();
        GenerateLogTab();
    }

    /// <summary>生成 16 字节随机挑战。</summary>
    public static byte[] GenerateChallenge()
    {
        var challenge = new byte[BlockSize];
        RandomNumberGenerator.Fill(challenge);
        return challenge;
    }

    /// <summary>用传入挑战生成密钥调度，并加密固定序列得到鉴权响应。</summary>
    public byte[] ComputeChallengeResponse(byte[] challenge)
    {
        byte[][] keys = KeySchedule((byte[])challenge.Clone());
        return Encrypt(Sequence, keys);
    }

    /// <summary>按 Pattern 0x9999 的位模式决定该位用异或还是加法运算。</summary>
    private static bool BitSet(int index) => ((1 << index) & Pattern) != 0;

    /// <summary>生成 SAFER+ 轮密钥所需的偏置矩阵。</summary>
    private void GenerateBiasMatrix()
    {
        for (int i = 0; i < 16; i++)
        {
            var biasVec = new byte[16];
            for (int j = 0; j < 16; j++)
            {
                int exponent = 17 * (i + 2) + (j + 1);
                int inner = ModPow(45, exponent, 257);
                int value = ModPow(45, inner, 257);
                biasVec[j] = (byte)(value == 256 ? 0 : value);
            }

            _biasMatrix[i] = biasVec;
        }
    }

    /// <summary>生成 45 的幂指数表（GF(257) 乘法展开）。</summary>
    private void GenerateExpTab()
    {
        for (int i = 0; i < 256; i++)
        {
            int value = ModPow(45, i, 257);
            _expTab[i] = (byte)(i == 128 ? 0 : value);
        }
    }

    /// <summary>生成对数表，配合指数表做快速乘法。</summary>
    private void GenerateLogTab()
    {
        for (int i = 0; i < 256; i++)
        {
            if (i == 0)
            {
                _logTab[i] = 128;
                continue;
            }

            int value = ModPow(45, i, 257);
            if (value != 256)
            {
                _logTab[value] = (byte)i;
            }
        }
    }

    /// <summary>
    /// 密钥调度：对初始密钥做异或修正，放入 17 字节寄存器，
    /// 每轮按索引取寄存器偏移值并叠加偏置矩阵得到轮密钥。
    /// </summary>
    private byte[][] KeySchedule(byte[] keyInit)
    {
        keyInit[15] ^= 6;

        var keys = new byte[17][];
        keys[0] = keyInit;

        var register = new byte[17];
        keyInit.CopyTo(register, 0);
        byte xor = 0;
        foreach (byte b in keyInit)
        {
            xor ^= b;
        }
        register[16] = xor;

        for (int keyIdx = 1; keyIdx < keys.Length; keyIdx++)
        {
            for (int i = 0; i < 17; i++)
            {
                register[i] = RotateLeft5(register[i]);
            }

            var keyI = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                keyI[i] = (byte)(register[(keyIdx + i) % 17] + _biasMatrix[keyIdx - 1][i]);
            }

            keys[keyIdx] = keyI;
        }

        return keys;
    }

    /// <summary>寄存器单字节循环左移 5 位。</summary>
    private static byte RotateLeft5(byte value)
    {
        int v = value & 0xFF;
        return (byte)(((v >> 5) | (v << 3)) & 0xFF);
    }

    /// <summary>
    /// SAFER+ 8 轮加密：每轮依次做密钥混合、查表代换、
    /// 密钥混合与线性变换，最后再叠加第 17 组密钥。
    /// </summary>
    private byte[] Encrypt(byte[] plaintext, byte[][] keys)
    {
        var ciphertext = (byte[])plaintext.Clone();
        for (int round = 0; round < 8; round++)
        {
            if (round == 2)
            {
                for (int i = 0; i < BlockSize; i++)
                {
                    ciphertext[i] = BitSet(i)
                        ? (byte)(ciphertext[i] ^ plaintext[i])
                        : (byte)(ciphertext[i] + plaintext[i]);
                }
            }

            for (int i = 0; i < BlockSize; i++)
            {
                ciphertext[i] = BitSet(i)
                    ? (byte)(ciphertext[i] ^ keys[round * 2][i])
                    : (byte)(ciphertext[i] + keys[round * 2][i]);
            }

            for (int i = 0; i < BlockSize; i++)
            {
                ciphertext[i] = BitSet(i)
                    ? _expTab[ciphertext[i] & 0xFF]
                    : _logTab[ciphertext[i] & 0xFF];
            }

            for (int i = 0; i < BlockSize; i++)
            {
                ciphertext[i] = BitSet(i)
                    ? (byte)(keys[round * 2 + 1][i] + ciphertext[i])
                    : (byte)(keys[round * 2 + 1][i] ^ ciphertext[i]);
            }

            var copy = (byte[])ciphertext.Clone();
            for (int i = 0; i < BlockSize; i++)
            {
                byte sum = 0;
                for (int j = 0; j < BlockSize; j++)
                {
                    sum = (byte)(sum + (byte)(Coefficients[i][j] * copy[j]));
                }

                ciphertext[i] = sum;
            }
        }

        for (int i = 0; i < BlockSize; i++)
        {
            ciphertext[i] = BitSet(i)
                ? (byte)(keys[16][i] ^ ciphertext[i])
                : (byte)(keys[16][i] + ciphertext[i]);
        }

        return ciphertext;
    }

    /// <summary>快速模幂运算，用于 GF(257) 下的查表生成。</summary>
    private static int ModPow(int value, int exponent, int modulus)
    {
        long result = 1;
        long baseValue = value % modulus;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
            {
                result = (result * baseValue) % modulus;
            }

            baseValue = (baseValue * baseValue) % modulus;
            exponent >>= 1;
        }

        return (int)result;
    }
}

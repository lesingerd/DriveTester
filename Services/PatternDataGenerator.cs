using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DriveTester.Services;

public static class PatternDataGenerator
{
    public const ulong MAGIC_SIGNATURE = 0x2154534554565244; // "DRVTEST!" in little endian
    public const int SUB_BLOCK_SIZE = 64 * 1024; // 64 KB sub-blocks
    public const int HEADER_SIZE = 48;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void FillBuffer(Span<byte> buffer, int round, int fileIndex, long fileOffsetStart, ulong baseSeed)
    {
        int bytesWritten = 0;
        int total = buffer.Length;
        long currentOffset = fileOffsetStart;

        while (bytesWritten < total)
        {
            int chunkSize = Math.Min(SUB_BLOCK_SIZE, total - bytesWritten);
            Span<byte> subBlock = buffer.Slice(bytesWritten, chunkSize);

            long blockIndex = currentOffset / SUB_BLOCK_SIZE;
            ulong blockSeed = baseSeed ^ (ulong)round ^ ((ulong)fileIndex << 16) ^ ((ulong)blockIndex << 32);

            // Write 48-byte header if chunk is at least HEADER_SIZE
            if (chunkSize >= HEADER_SIZE)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(subBlock.Slice(0, 8), MAGIC_SIGNATURE);
                BinaryPrimitives.WriteInt32LittleEndian(subBlock.Slice(8, 4), round);
                BinaryPrimitives.WriteInt32LittleEndian(subBlock.Slice(12, 4), fileIndex);
                BinaryPrimitives.WriteInt64LittleEndian(subBlock.Slice(16, 8), blockIndex);
                BinaryPrimitives.WriteInt64LittleEndian(subBlock.Slice(24, 8), currentOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(subBlock.Slice(32, 8), blockSeed);
                // SubBlock[40..48] will hold checksum calculated after body fill
            }

            // Fill body with high-entropy deterministic pseudo-random bytes (XorShift64)
            ulong state = blockSeed != 0 ? blockSeed : 0x8543789012345678UL;
            int bodyStart = Math.Min(HEADER_SIZE, chunkSize);
            int bodyLength = chunkSize - bodyStart;

            int bodyIters = bodyLength / 8;
            for (int i = 0; i < bodyIters; i++)
            {
                // XorShift64*
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                ulong val = state * 0x2545F4914F6CDD1DUL;
                BinaryPrimitives.WriteUInt64LittleEndian(subBlock.Slice(bodyStart + i * 8, 8), val);
            }

            // Remainder bytes
            int rem = bodyLength % 8;
            if (rem > 0)
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                ulong val = state * 0x2545F4914F6CDD1DUL;
                for (int r = 0; r < rem; r++)
                {
                    subBlock[bodyStart + bodyIters * 8 + r] = (byte)(val >> (r * 8));
                }
            }

            // Calculate fast 64-bit checksum of the body and record in header
            if (chunkSize >= HEADER_SIZE)
            {
                ulong checksum = ComputeBlockChecksum(subBlock.Slice(HEADER_SIZE, bodyLength), blockSeed);
                BinaryPrimitives.WriteUInt64LittleEndian(subBlock.Slice(40, 8), checksum);
            }

            bytesWritten += chunkSize;
            currentOffset += chunkSize;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (bool isValid, string? errorMessage) VerifyBuffer(
        ReadOnlySpan<byte> buffer,
        int expectedRound,
        int expectedFileIndex,
        long expectedFileOffsetStart,
        ulong baseSeed)
    {
        int bytesVerified = 0;
        int total = buffer.Length;
        long currentOffset = expectedFileOffsetStart;

        Span<byte> scratchChunk = stackalloc byte[HEADER_SIZE];

        while (bytesVerified < total)
        {
            int chunkSize = Math.Min(SUB_BLOCK_SIZE, total - bytesVerified);
            ReadOnlySpan<byte> subBlock = buffer.Slice(bytesVerified, chunkSize);
            long blockIndex = currentOffset / SUB_BLOCK_SIZE;
            ulong expectedBlockSeed = baseSeed ^ (ulong)expectedRound ^ ((ulong)expectedFileIndex << 16) ^ ((ulong)blockIndex << 32);

            if (chunkSize >= HEADER_SIZE)
            {
                ulong magic = BinaryPrimitives.ReadUInt64LittleEndian(subBlock.Slice(0, 8));
                if (magic != MAGIC_SIGNATURE)
                {
                    // Check if all zero
                    if (IsAllZero(subBlock))
                    {
                        return (false, $"Block at offset {currentOffset:N0} (File #{expectedFileIndex}) contains all zeroes! Data was dropped or unwritten.");
                    }

                    return (false, $"Corrupted header at offset {currentOffset:N0} in File #{expectedFileIndex}. Expected signature 'DRVTEST!' but read 0x{magic:X16}.");
                }

                int readRound = BinaryPrimitives.ReadInt32LittleEndian(subBlock.Slice(8, 4));
                int readFileIndex = BinaryPrimitives.ReadInt32LittleEndian(subBlock.Slice(12, 4));
                long readBlockIndex = BinaryPrimitives.ReadInt64LittleEndian(subBlock.Slice(16, 8));
                long readOffset = BinaryPrimitives.ReadInt64LittleEndian(subBlock.Slice(24, 8));
                ulong readSeed = BinaryPrimitives.ReadUInt64LittleEndian(subBlock.Slice(32, 8));
                ulong readChecksum = BinaryPrimitives.ReadUInt64LittleEndian(subBlock.Slice(40, 8));

                // Check for ghost/wrap-around data (crucial fake drive indicator)
                if (readFileIndex != expectedFileIndex || readRound != expectedRound || readBlockIndex != blockIndex)
                {
                    return (false, $"Looping/Fake capacity detected! At offset {currentOffset:N0}, expected File #{expectedFileIndex} (Round {expectedRound}, Block {blockIndex}), but found data from File #{readFileIndex} (Round {readRound}, Block {readBlockIndex})! Drive firmware wrapped around capacity.");
                }

                if (readSeed != expectedBlockSeed)
                {
                    return (false, $"Seed mismatch at offset {currentOffset:N0}. Expected 0x{expectedBlockSeed:X16}, got 0x{readSeed:X16}.");
                }

                int bodyLength = chunkSize - HEADER_SIZE;
                ulong calculatedChecksum = ComputeBlockChecksum(subBlock.Slice(HEADER_SIZE, bodyLength), readSeed);
                if (readChecksum != calculatedChecksum)
                {
                    return (false, $"Checksum failure in block at offset {currentOffset:N0} (File #{expectedFileIndex}). Recorded checksum 0x{readChecksum:X16} != computed 0x{calculatedChecksum:X16}. Bit flip or corrupted flash cell.");
                }
            }
            else
            {
                // Verify small trailing chunk against regenerated pattern
                Span<byte> expectedChunk = scratchChunk.Slice(0, chunkSize);
                FillBuffer(expectedChunk, expectedRound, expectedFileIndex, currentOffset, baseSeed);
                if (!subBlock.SequenceEqual(expectedChunk))
                {
                    return (false, $"Data mismatch in trailing bytes at offset {currentOffset:N0} in File #{expectedFileIndex}.");
                }
            }

            bytesVerified += chunkSize;
            currentOffset += chunkSize;
        }

        return (true, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeBlockChecksum(ReadOnlySpan<byte> span, ulong seed)
    {
        ulong hash = seed ^ 0xCBF29CE484222325UL;
        int iters = span.Length / 8;
        for (int i = 0; i < iters; i++)
        {
            ulong val = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(i * 8, 8));
            hash ^= val;
            hash *= 0x100000001B3UL;
        }
        int rem = span.Length % 8;
        if (rem > 0)
        {
            for (int r = 0; r < rem; r++)
            {
                hash ^= span[iters * 8 + r];
                hash *= 0x100000001B3UL;
            }
        }
        return hash;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        int iters = span.Length / 8;
        for (int i = 0; i < iters; i++)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(i * 8, 8)) != 0)
                return false;
        }
        for (int i = iters * 8; i < span.Length; i++)
        {
            if (span[i] != 0) return false;
        }
        return true;
    }
}

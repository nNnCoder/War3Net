// ------------------------------------------------------------------------------
// <copyright file="MpqStream.cs" company="Drake53">
// Licensed under the MIT license.
// See the LICENSE file in the project root for more information.
// </copyright>
// ------------------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

using War3Net.Common.Extensions;
using War3Net.IO.Compression;
using War3Net.IO.Mpq.Extensions;

namespace War3Net.IO.Mpq
{
    /// <summary>
    /// A Stream based class for reading a file from an <see cref="MpqArchive"/>.
    /// </summary>
    public class MpqStream : Stream
    {
        private readonly Stream _stream;
        private readonly int _blockSize;
        private readonly bool _canRead;
        private readonly uint[] _blockPositions = Array.Empty<uint>();
        private readonly bool _isStreamOwner;

        // MpqEntry data
        private readonly uint _filePosition;
        private readonly uint _fileSize;
        private readonly uint _compressedSize;
        private readonly MpqFileFlags _flags;
        private readonly bool _isCompressed;
        private readonly bool _isEncrypted;
        private readonly bool _isSingleUnit;
        private readonly uint _encryptionSeed;
        private readonly uint _baseEncryptionSeed;

        private byte[]? _currentData;
        private long _position;
        private int _currentBlockIndex;

        /// <summary>
        /// Initializes a new instance of the <see cref="MpqStream"/> class.
        /// </summary>
        /// <param name="archive">The archive from which to load a file.</param>
        /// <param name="entry">The file's entry in the <see cref="BlockTable"/>.</param>
        internal MpqStream(MpqArchive archive, MpqEntry entry)
            : this(entry, archive.BaseStream, archive.BlockSize)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MpqStream"/> class.
        /// </summary>
        /// <param name="entry">The file's entry in the <see cref="BlockTable"/>.</param>
        /// <param name="baseStream">The <see cref="MpqArchive"/>'s stream.</param>
        /// <param name="blockSize">The <see cref="MpqArchive.BlockSize"/>.</param>
        internal MpqStream(MpqEntry entry, Stream baseStream, int blockSize)
        {
            _canRead = true;
            _isStreamOwner = false;

            _filePosition = entry.FilePosition;
            _fileSize = entry.FileSize;
            _compressedSize = entry.CompressedSize;
            _flags = entry.Flags;
            _isCompressed = (_flags & MpqFileFlags.Compressed) != 0;
            _isEncrypted = _flags.HasFlag(MpqFileFlags.Encrypted);
            _isSingleUnit = _flags.HasFlag(MpqFileFlags.SingleUnit);

            _encryptionSeed = entry.EncryptionSeed;
            _baseEncryptionSeed = entry.BaseEncryptionSeed;

            _stream = baseStream;
            _blockSize = blockSize;

            if (_isSingleUnit)
            {
                if (!TryPeekCompressionType(out var mpqCompressionType) ||
                    (mpqCompressionType.HasValue && !mpqCompressionType.Value.IsKnownMpqCompressionType()))
                {
                    _canRead = false;
                }
            }
            else
            {
                _currentBlockIndex = -1;

                // Compressed files start with an array of offsets to make seeking possible
                if (_isCompressed)
                {
                    var blockPositionsCount = (int)((_fileSize + _blockSize - 1) / _blockSize) + 1;

                    // Files with metadata have an extra block containing block checksums
                    if (_flags.HasFlag(MpqFileFlags.FileHasMetadata))
                    {
                        blockPositionsCount++;
                    }

                    _blockPositions = new uint[blockPositionsCount];

                    lock (_stream)
                    {
                        _stream.Seek(_filePosition, SeekOrigin.Begin);
                        using (var binaryReader = new BinaryReader(_stream, Encoding.UTF8, true))
                        {
                            for (var i = 0; i < _blockPositions.Length; i++)
                            {
                                _blockPositions[i] = binaryReader.ReadUInt32();
                            }
                        }
                    }

                    var expectedOffsetFirstBlock = (uint)_blockPositions.Length * 4;

                    if (_isEncrypted && _blockPositions.Length > 1)
                    {
                        var maxOffsetSecondBlock = (uint)_blockSize + expectedOffsetFirstBlock;
                        if (_encryptionSeed == 0)
                        {
                            // This should only happen when the file name is not known.
                            if (!entry.TryUpdateEncryptionSeed(_blockPositions[0], _blockPositions[1], expectedOffsetFirstBlock, maxOffsetSecondBlock))
                            {
                                _canRead = false;
                                return;
                            }
                        }

                        _encryptionSeed = entry.EncryptionSeed;
                        _baseEncryptionSeed = entry.BaseEncryptionSeed;
                        StormBuffer.DecryptBlock(_blockPositions, _encryptionSeed - 1);
                    }

                    var currentPosition = _blockPositions[0];
                    for (var i = 1; i < _blockPositions.Length; i++)
                    {
                        var currentBlockSize = _blockPositions[i] - currentPosition;

                        if (currentBlockSize <= 0 || currentBlockSize > _blockSize)
                        {
                            _canRead = false;
                            break;
                        }

                        var expectedlength = Math.Min((int)(Length - ((i - 1) * _blockSize)), _blockSize);
                        if (!TryPeekCompressionType(i - 1, expectedlength, out var mpqCompressionType) ||
                            (mpqCompressionType.HasValue && !mpqCompressionType.Value.IsKnownMpqCompressionType()))
                        {
                            _canRead = false;
                            break;
                        }

                        currentPosition = _blockPositions[i];
                    }
                }
                else if (_isEncrypted && _fileSize >= 4 && _encryptionSeed == 0)
                {
                    _canRead = false;
                }
            }
        }

        internal MpqStream(Stream baseStream, string? fileName, bool leaveOpen = false)
            : this(new MpqEntry(fileName, 0, 0, (uint)baseStream.Length, (uint)baseStream.Length, MpqFileFlags.Exists | MpqFileFlags.SingleUnit), baseStream, 0)
        {
            _isStreamOwner = !leaveOpen;
        }

        /// <summary>
        /// Re-encodes the stream using the given parameters.
        /// </summary>
        internal Stream Transform(MpqFileFlags targetFlags, MpqCompressionType compressionType, uint targetFilePosition, int targetBlockSize)
        {
            using var memoryStream = new MemoryStream();
            CopyTo(memoryStream);
            memoryStream.Position = 0;
            var fileSize = memoryStream.Length;

            using var compressedStream = GetCompressedStream(memoryStream, targetFlags, compressionType, targetBlockSize);
            var compressedSize = (uint)compressedStream.Length;

            var resultStream = new MemoryStream();

            var blockPosCount = (uint)(((int)fileSize + targetBlockSize - 1) / targetBlockSize) + 1;
            if (targetFlags.HasFlag(MpqFileFlags.Encrypted) && blockPosCount > 1)
            {
                var blockPositions = new int[blockPosCount];
                var singleUnit = targetFlags.HasFlag(MpqFileFlags.SingleUnit);

                var hasBlockPositions = !singleUnit && ((targetFlags & MpqFileFlags.Compressed) != 0);
                if (hasBlockPositions)
                {
                    for (var blockIndex = 0; blockIndex < blockPosCount; blockIndex++)
                    {
                        using (var br = new BinaryReader(compressedStream, new UTF8Encoding(), true))
                        {
                            for (var i = 0; i < blockPosCount; i++)
                            {
                                blockPositions[i] = (int)br.ReadUInt32();
                            }
                        }

                        compressedStream.Seek(0, SeekOrigin.Begin);
                    }
                }
                else
                {
                    if (singleUnit)
                    {
                        blockPosCount = 2;
                    }

                    blockPositions[0] = 0;
                    for (var blockIndex = 2; blockIndex < blockPosCount; blockIndex++)
                    {
                        blockPositions[blockIndex - 1] = targetBlockSize * (blockIndex - 1);
                    }

                    blockPositions[blockPosCount - 1] = (int)compressedSize;
                }

                var encryptionSeed = _baseEncryptionSeed;
                if (targetFlags.HasFlag(MpqFileFlags.BlockOffsetAdjustedKey))
                {
                    encryptionSeed = MpqEntry.AdjustEncryptionSeed(encryptionSeed, targetFilePosition, (uint)fileSize);
                }

                var currentOffset = 0;
                using (var writer = new BinaryWriter(resultStream, new UTF8Encoding(false, true), true))
                {
                    for (var blockIndex = hasBlockPositions ? 0 : 1; blockIndex < blockPosCount; blockIndex++)
                    {
                        var toWrite = blockPositions[blockIndex] - currentOffset;

                        var data = StormBuffer.EncryptStream(compressedStream, (uint)(encryptionSeed + blockIndex - 1), currentOffset, toWrite);
                        writer.Write(data);

                        currentOffset += toWrite;
                    }
                }
            }
            else
            {
                compressedStream.CopyTo(resultStream);
            }

            resultStream.Position = 0;
            return resultStream;
        }

        private Stream GetCompressedStream(Stream baseStream, MpqFileFlags targetFlags, MpqCompressionType compressionType, int targetBlockSize)
        {
            var resultStream = new MemoryStream();
            var singleUnit = targetFlags.HasFlag(MpqFileFlags.SingleUnit);

            void TryCompress(uint bytes)
            {
                var offset = baseStream.Position;
                var compressedStream = compressionType switch
                {
                    MpqCompressionType.ZLib => ZLibCompression.Compress(baseStream, (int)bytes, true),

                    _ => throw new NotSupportedException(),
                };

                // Add one because CompressionType byte not written yet.
                var length = compressedStream.Length + 1;
                if (!singleUnit && length >= bytes)
                {
                    baseStream.CopyTo(resultStream, offset, (int)bytes, StreamExtensions.DefaultBufferSize);
                }
                else
                {
                    resultStream.WriteByte((byte)compressionType);
                    compressedStream.Position = 0;
                    compressedStream.CopyTo(resultStream);
                }

                compressedStream.Dispose();

                if (singleUnit)
                {
                    baseStream.Dispose();
                }
            }

            var length = (uint)baseStream.Length;

            if ((targetFlags & MpqFileFlags.Compressed) == 0)
            {
                baseStream.CopyTo(resultStream);
            }
            else if (singleUnit)
            {
                TryCompress(length);
            }
            else
            {
                var blockCount = (uint)((length + targetBlockSize - 1) / targetBlockSize) + 1;
                var blockOffsets = new uint[blockCount];

                blockOffsets[0] = 4 * blockCount;
                resultStream.Position = blockOffsets[0];

                for (var blockIndex = 1; blockIndex < blockCount; blockIndex++)
                {
                    var bytesToCompress = blockIndex + 1 == blockCount ? (uint)(baseStream.Length - baseStream.Position) : (uint)targetBlockSize;

                    TryCompress(bytesToCompress);
                    blockOffsets[blockIndex] = (uint)resultStream.Position;
                }

                resultStream.Position = 0;
                using (var writer = new BinaryWriter(resultStream, new UTF8Encoding(false, true), true))
                {
                    for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
                    {
                        writer.Write(blockOffsets[blockIndex]);
                    }
                }
            }

            resultStream.Position = 0;
            return resultStream;
        }

        public MpqFileFlags Flags => _flags;

        public bool IsCompressed => _isCompressed;

        public bool IsEncrypted => _isEncrypted;

        [Obsolete("Use CanRead instead.")]
        public bool CanBeDecrypted => !_isEncrypted || _fileSize < 4 || _encryptionSeed != 0;

        public uint CompressedSize => _compressedSize;

        public uint FileSize => _fileSize;

        public uint FilePosition => _filePosition;

        public int BlockSize => _blockSize;

        /// <inheritdoc/>
        public override bool CanRead => _canRead;

        /// <inheritdoc/>
        public override bool CanSeek => _canRead;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _fileSize;

        /// <inheritdoc/>
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        /// <inheritdoc/>
        public override void Flush()
        {
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek)
            {
                throw new NotSupportedException();
            }

            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,

                _ => throw new InvalidEnumArgumentException(nameof(origin), (int)origin, typeof(SeekOrigin)),
            };

            if (target < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Attempted to Seek before the beginning of the stream");
            }

            if (target > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Attempted to Seek beyond the end of the stream");
            }

            return _position = target;
        }

        /// <inheritdoc/>
        public override void SetLength(long value)
        {
            throw new NotSupportedException("SetLength is not supported");
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!CanRead)
            {
                throw new NotSupportedException();
            }

            if (_isSingleUnit)
            {
                return ReadInternal(buffer, offset, count);
            }

            var toread = count;
            var readtotal = 0;

            while (toread > 0)
            {
                var read = ReadInternal(buffer, offset, toread);
                if (read == 0)
                {
                    break;
                }

                readtotal += read;
                offset += read;
                toread -= read;
            }

            return readtotal;
        }

        /// <inheritdoc/>
        public override int ReadByte()
        {
            if (!CanRead)
            {
                throw new NotSupportedException();
            }

            if (_position >= Length)
            {
                return -1;
            }

            BufferData();
            return _currentData[_isSingleUnit ? _position++ : (int)(_position++ & (_blockSize - 1))];
        }

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("Write is not supported");
        }

        public override void Close()
        {
            base.Close();
            if (_isStreamOwner)
            {
                _stream.Close();
            }
        }

        /// <summary>
        /// Copy the base stream, so that the contents do not get decompressed not decrypted.
        /// </summary>
        internal void CopyBaseStreamTo(Stream target)
        {
            lock (_stream)
            {
                _stream.CopyTo(target, _filePosition, (int)_compressedSize, StreamExtensions.DefaultBufferSize);
            }
        }

        private static byte[] DecompressMulti(byte[] input, uint outputLength)
        {
            using var memoryStream = new MemoryStream(input);
            return GetDecompressionFunction((MpqCompressionType)memoryStream.ReadByte(), outputLength).Invoke(memoryStream);
        }

        private static Func<Stream, byte[]> GetDecompressionFunction(MpqCompressionType compressionType, uint outputLength)
        {
            return compressionType switch
            {
                MpqCompressionType.Huffman => HuffmanCoding.Decompress,
                MpqCompressionType.ZLib => (stream) => ZLibCompression.Decompress(stream, outputLength),
                MpqCompressionType.PKLib => (stream) => PKDecompress(stream, outputLength),
                MpqCompressionType.BZip2 => (stream) => BZip2Compression.Decompress(stream, outputLength),
                MpqCompressionType.Lzma => throw new NotImplementedException("LZMA compression is not yet supported"),
                MpqCompressionType.Sparse => throw new NotImplementedException("Sparse compression is not yet supported"),
                MpqCompressionType.ImaAdpcmMono => (stream) => AdpcmCompression.Decompress(stream, 1),
                MpqCompressionType.ImaAdpcmStereo => (stream) => AdpcmCompression.Decompress(stream, 2),

                MpqCompressionType.Sparse | MpqCompressionType.ZLib => throw new NotImplementedException("Sparse compression + Deflate compression is not yet supported"),
                MpqCompressionType.Sparse | MpqCompressionType.BZip2 => throw new NotImplementedException("Sparse compression + BZip2 compression is not yet supported"),

                MpqCompressionType.ImaAdpcmMono | MpqCompressionType.Huffman => (stream) => AdpcmCompression.Decompress(HuffmanCoding.Decompress(stream), 1),
                MpqCompressionType.ImaAdpcmMono | MpqCompressionType.PKLib => (stream) => AdpcmCompression.Decompress(PKDecompress(stream, outputLength), 1),

                MpqCompressionType.ImaAdpcmStereo | MpqCompressionType.Huffman => (stream) => AdpcmCompression.Decompress(HuffmanCoding.Decompress(stream), 2),
                MpqCompressionType.ImaAdpcmStereo | MpqCompressionType.PKLib => (stream) => AdpcmCompression.Decompress(PKDecompress(stream, outputLength), 2),

                _ => throw new NotSupportedException($"Compression of type 0x{compressionType.ToString("X")} is not yet supported"),
            };
        }

        private static byte[] PKDecompress(Stream data, uint expectedLength)
        {
            var b1 = data.ReadByte();
            var b2 = data.ReadByte();
            var b3 = data.ReadByte();
            if (b1 == 0 && b2 == 0 && b3 == 0)
            {
                using (var reader = new BinaryReader(data))
                {
                    var expectedStreamLength = reader.ReadUInt32();
                    if (expectedStreamLength != data.Length)
                    {
                        throw new InvalidDataException("Unexpected stream length value");
                    }

                    if (expectedLength + 8 == expectedStreamLength)
                    {
                        // Assume data is not compressed.
                        return reader.ReadBytes((int)expectedLength);
                    }

                    var comptype = (MpqCompressionType)reader.ReadByte();
                    if (comptype != MpqCompressionType.ZLib)
                    {
                        throw new NotImplementedException();
                    }

                    return ZLibCompression.Decompress(data, expectedLength);
                }
            }
            else
            {
                data.Seek(-3, SeekOrigin.Current);
                return PKLibCompression.Decompress(data, expectedLength);
            }
        }

        private int ReadInternal(byte[] buffer, int offset, int count)
        {
            // OW: avoid reading past the contents of the file
            if (_position >= Length)
            {
                return 0;
            }

            BufferData();

            var localposition = _isSingleUnit ? _position : (_position & (_blockSize - 1));
            var canRead = (int)(_currentData.Length - localposition);
            var bytestocopy = canRead > count ? count : canRead;
            if (bytestocopy <= 0)
            {
                return 0;
            }

            Array.Copy(_currentData, localposition, buffer, offset, bytestocopy);

            _position += bytestocopy;
            return bytestocopy;
        }

        [MemberNotNull(nameof(_currentData))]
        private void BufferData()
        {
            if (!_isSingleUnit)
            {
                var requiredblock = (int)(_position / _blockSize);
                if (requiredblock != _currentBlockIndex || _currentData is null)
                {
                    var expectedlength = Math.Min((int)(Length - (requiredblock * _blockSize)), _blockSize);
                    _currentData = LoadBlock(requiredblock, expectedlength);
                    _currentBlockIndex = requiredblock;
                }
            }
            else if (_currentData is null)
            {
                _currentData = LoadSingleUnit();
            }
        }

        private byte[] LoadSingleUnit()
        {
            // Read the entire file into memory
            var filedata = new byte[_compressedSize];
            lock (_stream)
            {
                _stream.Seek(_filePosition, SeekOrigin.Begin);
                var read = _stream.Read(filedata, 0, filedata.Length);
                if (read != filedata.Length)
                {
                    throw new MpqParserException("Insufficient data or invalid data length");
                }
            }

            if (_isEncrypted && _fileSize > 3)
            {
                if (_encryptionSeed == 0)
                {
                    throw new MpqParserException("Unable to determine encryption key");
                }

                StormBuffer.DecryptBlock(filedata, _encryptionSeed);
            }

            return _flags.HasFlag(MpqFileFlags.CompressedMulti) && _compressedSize > 0
                ? DecompressMulti(filedata, _fileSize)
                : filedata;
        }

        private byte[] LoadBlock(int blockIndex, int expectedLength)
        {
            long offset;
            int bufferSize;

            if (_isCompressed)
            {
                offset = _blockPositions[blockIndex];
                bufferSize = (int)(_blockPositions[blockIndex + 1] - offset);
            }
            else
            {
                offset = (uint)(blockIndex * _blockSize);
                bufferSize = expectedLength;
            }

            offset += _filePosition;

            var buffer = new byte[bufferSize];
            lock (_stream)
            {
                _stream.Seek(offset, SeekOrigin.Begin);
                var read = _stream.Read(buffer, 0, bufferSize);
                if (read != bufferSize)
                {
                    throw new MpqParserException("Insufficient data or invalid data length");
                }
            }

            if (_isEncrypted && bufferSize > 3)
            {
                if (_encryptionSeed == 0)
                {
                    throw new MpqParserException("Unable to determine encryption key");
                }

                StormBuffer.DecryptBlock(buffer, (uint)(blockIndex + _encryptionSeed));
            }

            if (_isCompressed && (bufferSize != expectedLength))
            {
                buffer = _flags.HasFlag(MpqFileFlags.CompressedPK)
                    ? PKLibCompression.Decompress(buffer, (uint)expectedLength)
                    : DecompressMulti(buffer, (uint)expectedLength);
            }

            return buffer;
        }

        private bool TryPeekCompressionType(out MpqCompressionType? mpqCompressionType)
        {
            var bufferSize = Math.Min((int)_compressedSize, 4);

            var buffer = new byte[bufferSize];
            lock (_stream)
            {
                _stream.Seek(_filePosition, SeekOrigin.Begin);
                var read = _stream.Read(buffer, 0, bufferSize);
                if (read != bufferSize)
                {
                    mpqCompressionType = null;
                    return false;
                }
            }

            if (_isEncrypted && bufferSize > 3)
            {
                if (_encryptionSeed == 0)
                {
                    mpqCompressionType = null;
                    return false;
                }

                StormBuffer.DecryptBlock(buffer, _encryptionSeed);
            }

            if (_flags.HasFlag(MpqFileFlags.CompressedMulti) && bufferSize > 0)
            {
                mpqCompressionType = (MpqCompressionType)buffer[0];
                return true;
            }

            mpqCompressionType = null;
            return true;
        }

        private bool TryPeekCompressionType(int blockIndex, int expectedLength, out MpqCompressionType? mpqCompressionType)
        {
            var offset = _blockPositions[blockIndex];
            var bufferSize = (int)(_blockPositions[blockIndex + 1] - offset);

            if (bufferSize == expectedLength)
            {
                mpqCompressionType = null;
                return !_isEncrypted || bufferSize < 4 || _encryptionSeed != 0;
            }

            offset += _filePosition;
            bufferSize = Math.Min(bufferSize, 4);

            var buffer = new byte[bufferSize];
            lock (_stream)
            {
                _stream.Seek(offset, SeekOrigin.Begin);
                var read = _stream.Read(buffer, 0, bufferSize);
                if (read != bufferSize)
                {
                    mpqCompressionType = null;
                    return false;
                }
            }

            if (_isEncrypted && bufferSize > 3)
            {
                if (_encryptionSeed == 0)
                {
                    mpqCompressionType = null;
                    return false;
                }

                StormBuffer.DecryptBlock(buffer, (uint)(blockIndex + _encryptionSeed));
            }

            if (_flags.HasFlag(MpqFileFlags.CompressedPK))
            {
                mpqCompressionType = null;
                return true;
            }

            mpqCompressionType = (MpqCompressionType)buffer[0];
            return true;
        }
    }
}
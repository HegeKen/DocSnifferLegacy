using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DocSnifferLegacy.Core.IO
{
    public sealed class ZipEntryInfo
    {
        public string Name;
        public long DataOffset;
        public long CompressedSize;
        public long UncompressedSize;
        public ushort Method;
        public bool IsDirectory;
    }

    /// <summary>
    /// 轻量 ZIP 读取器：仅支持传统 ZIP 结构（不支持 zip64），支持 store(0) 与 deflate(8)。
    /// 用途：OOXML 内部条目读取与后续压缩包穿透。非线程安全，同一基流同时只允许一个活动条目流。
    /// </summary>
    public static class ZipReader
    {
        private const uint CdeSig = 0x02014b50;
        private const uint LfhSig = 0x04034b50;

        public static List<ZipEntryInfo> ReadEntries(Stream stream)
        {
            if (!stream.CanSeek) throw new ArgumentException("ZipReader 需要可查找的流");
            long fileLen = stream.Length;

            int tailLen = (int)Math.Min(fileLen, 65536L + 22 + 4096);
            byte[] tail = new byte[tailLen];
            stream.Seek(fileLen - tailLen, SeekOrigin.Begin);
            ReadFully(stream, tail, tailLen);

            long cdOffset = -1;
            int entryCount = 0;
            for (int i = tailLen - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                {
                    entryCount = Le16(tail, i + 10);
                    cdOffset = Le32(tail, i + 16);
                    break;
                }
            }
            if (cdOffset < 0) throw new InvalidDataException("未找到 ZIP 中央目录（可能为 zip64 或文件损坏）");

            var entries = new List<ZipEntryInfo>(entryCount > 0 ? entryCount : 16);
            stream.Seek(cdOffset, SeekOrigin.Begin);
            byte[] cde = new byte[46];
            byte[] nameBuf = new byte[1024];
            for (int n = 0; n < entryCount; n++)
            {
                ReadFully(stream, cde, 46);
                if (Le32(cde, 0) != CdeSig) throw new InvalidDataException("ZIP 中央目录损坏");
                int flags = Le16(cde, 8);
                ushort method = (ushort)Le16(cde, 10);
                long csize = Le32(cde, 20);
                long usize = Le32(cde, 24);
                int nameLen = Le16(cde, 28);
                int extraLen = Le16(cde, 30);
                int commentLen = Le16(cde, 32);
                long lfhOffset = Le32(cde, 42);

                if (nameLen > nameBuf.Length) nameBuf = new byte[nameLen];
                ReadFully(stream, nameBuf, nameLen);
                string name = DecodeName(nameBuf, nameLen, flags);
                stream.Seek(extraLen + commentLen, SeekOrigin.Current);
                long cdNext = stream.Position; // 下一个中央目录条目的起点

                // 中央目录的 extra 长度可能与本地头不同，须按本地头重新定位数据起点
                stream.Seek(lfhOffset, SeekOrigin.Begin);
                byte[] lfh = new byte[30];
                ReadFully(stream, lfh, 30);
                if (Le32(lfh, 0) != LfhSig) throw new InvalidDataException("ZIP 本地文件头损坏: " + name);
                long dataOffset = lfhOffset + 30 + Le16(lfh, 26) + Le16(lfh, 28);
                stream.Seek(cdNext, SeekOrigin.Begin);

                entries.Add(new ZipEntryInfo
                {
                    Name = name,
                    Method = method,
                    CompressedSize = csize,
                    UncompressedSize = usize,
                    DataOffset = dataOffset,
                    IsDirectory = name.EndsWith("/", StringComparison.Ordinal)
                });
            }
            entries.Sort(delegate(ZipEntryInfo a, ZipEntryInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            return entries;
        }

        /// <summary>打开条目的解压/切片流。返回的流关闭后不会关闭基流。</summary>
        public static Stream OpenEntry(Stream stream, ZipEntryInfo entry)
        {
            if (entry.IsDirectory) throw new InvalidOperationException("目录条目没有数据流");
            PartialStream part = new PartialStream(stream, entry.DataOffset, entry.CompressedSize);
            if (entry.Method == 0) return part;
            if (entry.Method == 8) return new DeflateStream(part, CompressionMode.Decompress);
            throw new NotSupportedException("不支持的 ZIP 压缩方法: " + entry.Method);
        }

        private static string DecodeName(byte[] buf, int len, int flags)
        {
            if (len <= 0) return string.Empty;
            if ((flags & 0x800) != 0) return Encoding.UTF8.GetString(buf, 0, len);
            try { return new UTF8Encoding(false, true).GetString(buf, 0, len); }
            catch (Exception)
            {
                try { return Encoding.GetEncoding("GB18030").GetString(buf, 0, len); }
                catch (Exception) { return Encoding.ASCII.GetString(buf, 0, len); }
            }
        }

        private static void ReadFully(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int read = s.Read(buf, off, count - off);
                if (read <= 0) throw new EndOfStreamException("ZIP 数据不完整");
                off += read;
            }
        }

        private static int Le16(byte[] b, int off) { return b[off] | (b[off + 1] << 8); }
        private static long Le32(byte[] b, int off) { return (long)((uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24))); }
    }

    /// <summary>基流的只读切片。Close 不影响基流；按需惰性 Seek，适配顺序解压读取。</summary>
    internal sealed class PartialStream : Stream
    {
        private readonly Stream baseStream;
        private readonly long origin;
        private readonly long length;
        private long position;
        private bool positioned;

        public PartialStream(Stream baseStream, long origin, long length)
        {
            this.baseStream = baseStream;
            this.origin = origin;
            this.length = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!positioned)
            {
                baseStream.Seek(origin, SeekOrigin.Begin);
                positioned = true;
            }
            long remain = length - position;
            if (remain <= 0) return 0;
            if (count > remain) count = (int)remain;
            int read = baseStream.Read(buffer, offset, count);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin originKind)
        {
            long target = position;
            if (originKind == SeekOrigin.Begin) target = offset;
            else if (originKind == SeekOrigin.Current) target = position + offset;
            else target = length + offset;
            if (target < 0) target = 0;
            if (target > length) target = length;
            position = target;
            baseStream.Seek(origin + position, SeekOrigin.Begin);
            positioned = true;
            return position;
        }

        public override long Length { get { return length; } }
        public override long Position
        {
            get { return position; }
            set { Seek(value, SeekOrigin.Begin); }
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { /* 不关闭基流 */ }
    }
}

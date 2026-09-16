using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DocSnifferLegacy.Tests
{
    /// <summary>测试专用最小 ZIP 写入器：支持 store/deflate，用于构造 OOXML 夹具。</summary>
    internal sealed class TestZipWriter
    {
        private sealed class Item
        {
            public string Name;
            public byte[] Raw;
            public ushort Method;
            public uint Crc;
            public long LfhOffset;
            public long Csize;
            public long Usize;
        }

        private readonly MemoryStream output = new MemoryStream();
        private readonly List<Item> items = new List<Item>();

        public void Add(string name, string content, bool deflate)
        {
            byte[] raw = new UTF8Encoding(false).GetBytes(content);
            Add(name, raw, deflate);
        }

        public void Add(string name, byte[] raw, bool deflate)
        {
            var item = new Item { Name = name, Raw = raw, Method = (ushort)(deflate ? 8 : 0) };
            item.Crc = Crc32.Compute(raw, 0, raw.Length);
            item.Usize = raw.Length;
            items.Add(item);
        }

        public byte[] Build()
        {
            var body = new MemoryStream();
            foreach (Item item in items)
            {
                item.LfhOffset = body.Position;
                byte[] nameBytes = new UTF8Encoding(false).GetBytes(item.Name);
                byte[] stored = item.Raw;
                if (item.Method == 8)
                {
                    var comp = new MemoryStream();
                    using (DeflateStream ds = new DeflateStream(comp, CompressionMode.Compress, true))
                    {
                        ds.Write(item.Raw, 0, item.Raw.Length);
                    }
                    stored = comp.ToArray();
                    // deflate 无收益时退回 store
                    if (stored.Length >= item.Raw.Length)
                    {
                        stored = item.Raw;
                        item.Method = 0;
                    }
                }
                item.Csize = stored.Length;

                WriteUint(body, 0x04034b50);
                WriteUshort(body, 20);          // version needed
                WriteUshort(body, 0x0800);      // flags: UTF-8 名称
                WriteUshort(body, item.Method);
                WriteUshort(body, 0);           // time
                WriteUshort(body, 0x21);        // date (1980-01-01)
                WriteUint(body, item.Crc);
                WriteUint(body, (uint)item.Csize);
                WriteUint(body, (uint)item.Usize);
                WriteUshort(body, (ushort)nameBytes.Length);
                WriteUshort(body, 0);           // extra len
                body.Write(nameBytes, 0, nameBytes.Length);
                body.Write(stored, 0, stored.Length);
            }

            long cdOffset = body.Position;
            long cdSize = 0;
            foreach (Item item in items)
            {
                byte[] nameBytes = new UTF8Encoding(false).GetBytes(item.Name);
                WriteUint(body, 0x02014b50);
                WriteUshort(body, 20);          // version made by
                WriteUshort(body, 20);          // version needed
                WriteUshort(body, 0x0800);
                WriteUshort(body, item.Method);
                WriteUshort(body, 0);
                WriteUshort(body, 0x21);
                WriteUint(body, item.Crc);
                WriteUint(body, (uint)item.Csize);
                WriteUint(body, (uint)item.Usize);
                WriteUshort(body, (ushort)nameBytes.Length);
                WriteUshort(body, 0);           // extra
                WriteUshort(body, 0);           // comment
                WriteUshort(body, 0);           // disk
                WriteUshort(body, 0);           // internal attrs
                WriteUint(body, 0);             // external attrs
                WriteUint(body, (uint)item.LfhOffset);
                body.Write(nameBytes, 0, nameBytes.Length);
                cdSize += 46 + nameBytes.Length;
            }

            WriteUint(body, 0x06054b50);
            WriteUshort(body, 0);
            WriteUshort(body, 0);
            WriteUshort(body, (ushort)items.Count);
            WriteUshort(body, (ushort)items.Count);
            WriteUint(body, (uint)cdSize);
            WriteUint(body, (uint)cdOffset);
            WriteUshort(body, 0);

            return body.ToArray();
        }

        private static void WriteUshort(MemoryStream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }

        private static void WriteUint(MemoryStream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    if ((c & 1) != 0) c = 0xEDB88320u ^ (c >> 1);
                    else c = c >> 1;
                }
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                crc = (crc >> 8) ^ Table[(crc & 0xFF) ^ data[i]];
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}

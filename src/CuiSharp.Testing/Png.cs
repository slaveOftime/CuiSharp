using System.IO.Compression;

namespace CuiSharp.Testing;

/// <summary>
/// Reads the PNG a golden image is stored as: eight bits per channel, RGBA, not interlaced — which
/// is what the app's own screenshot writer produces.
/// </summary>
internal static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];

    /// <summary>Reads an 8-bit RGBA PNG into a straight-alpha buffer.</summary>
    internal static bool TryDecode(byte[] png, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = 0;
        height = 0;

        if (png.Length < Signature.Length + 8 || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature)) return false;

        int at = Signature.Length;
        int bitDepth = 0, colorType = 0, interlace = 0;
        using var data = new MemoryStream();

        while (at + 12 <= png.Length)
        {
            int length = BigEndian(png, at);
            if (length < 0 || at + 12 + length > png.Length) return false;
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int body = at + 8;

            if (type == "IHDR")
            {
                width = BigEndian(png, body);
                height = BigEndian(png, body + 4);
                bitDepth = png[body + 8];
                colorType = png[body + 9];
                interlace = png[body + 12];
            }
            else if (type == "IDAT")
            {
                data.Write(png, body, length);
            }
            else if (type == "IEND")
            {
                break;
            }

            at = body + length + 4;
        }

        if (width <= 0 || height <= 0 || bitDepth != 8 || colorType != 6 || interlace != 0) return false;

        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        data.Position = 0;
        using (var inflate = new ZLibStream(data, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int got = inflate.Read(raw, read, raw.Length - read);
                if (got == 0) break;
                read += got;
            }

            if (read < raw.Length) return false;
        }

        rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            int source = (y * (stride + 1)) + 1;
            int target = y * stride;
            int previous = (y - 1) * stride;
            for (int x = 0; x < stride; x++)
            {
                int value = raw[source + x];
                int left = x >= 4 ? rgba[target + x - 4] : 0;
                int up = y > 0 ? rgba[previous + x] : 0;
                int upLeft = x >= 4 && y > 0 ? rgba[previous + x - 4] : 0;

                rgba[target + x] = filter switch
                {
                    0 => (byte)value,
                    1 => (byte)(value + left),
                    2 => (byte)(value + up),
                    3 => (byte)(value + ((left + up) / 2)),
                    4 => (byte)(value + Paeth(left, up, upLeft)),
                    _ => (byte)value,
                };
            }
        }

        return true;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toUpLeft = Math.Abs(estimate - upLeft);
        if (toLeft <= toUp && toLeft <= toUpLeft) return left;
        return toUp <= toUpLeft ? up : upLeft;
    }

    private static int BigEndian(byte[] bytes, int at) =>
        (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
}

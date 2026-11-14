using System.Runtime.InteropServices;
using BambuBar.Core.Camera.Decoding;

namespace BambuBar.Linux;

/// <summary>Encode private UI video in memory with the runtime's existing JPEG codec.</summary>
internal static partial class NativeCameraEncoding
{
    [LibraryImport("libgdk_pixbuf-2.0.so.0")]
    private static partial nint gdk_pixbuf_new_from_data(nint pixels, int colorspace, int alpha, int bits, int width, int height, int stride, nint destroy, nint data);
    [LibraryImport("libgdk_pixbuf-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int gdk_pixbuf_save_to_bufferv(nint pixbuf, out nint buffer, out nuint length, string type, nint keys, nint values, out nint error);
    [LibraryImport("libgobject-2.0.so.0")]
    private static partial void g_object_unref(nint value);
    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_free(nint value);
    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_error_free(nint value);

    public static unsafe byte[] Jpeg(CameraFrame frame)
    {
        var rgb = new byte[checked(frame.Width * frame.Height * 3)];
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var source = y * frame.Stride + x * 4; var target = (y * frame.Width + x) * 3;
                rgb[target] = frame.Pixels[source + 2]; rgb[target + 1] = frame.Pixels[source + 1]; rgb[target + 2] = frame.Pixels[source];
            }
        fixed (byte* pixels = rgb)
        {
            var pixbuf = gdk_pixbuf_new_from_data((nint)pixels, 0, 0, 8, frame.Width, frame.Height, frame.Width * 3, 0, 0);
            if (pixbuf == 0) throw new IOException("Camera image unavailable.");
            nint buffer = 0, error = 0;
            try
            {
                if (gdk_pixbuf_save_to_bufferv(pixbuf, out buffer, out var length, "jpeg", 0, 0, out error) == 0)
                    throw new IOException("Camera image could not be encoded.");
                var result = new byte[checked((int)length)];
                Marshal.Copy(buffer, result, 0, result.Length);
                return result;
            }
            finally { if (buffer != 0) g_free(buffer); if (error != 0) g_error_free(error); g_object_unref(pixbuf); }
        }
    }
}

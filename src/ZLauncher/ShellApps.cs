using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZLauncher;

/// <summary>An app the user added to Z Launcher (an .exe, a shortcut or a script).</summary>
sealed class AppEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public ImageSource? Icon { get; init; }
    public bool CanRunAsAdmin => Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    public void Launch(bool asAdmin = false)
    {
        var psi = new ProcessStartInfo(Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(Path) ?? "" };
        if (asAdmin) psi.Verb = "runas";
        Process.Start(psi);
    }

    public static AppEntry FromFile(string path, string? name = null) => new()
    {
        Path = path,
        Name = name ?? DefaultName(path),
        Icon = ShellIcons.ForFile(path, 64),
    };

    /// <summary>Shortcut name, or the exe's product description, or the file name.</summary>
    public static string DefaultName(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            }
            catch { }
        }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }
}

/// <summary>High-quality file icons (with transparency) through IShellItemImageFactory.</summary>
static class ShellIcons
{
    public static unsafe ImageSource? ForFile(string path, int size)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var obj);
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    static unsafe ImageSource? FromHBitmap(IntPtr hbm)
    {
        // DIBSECTION: BITMAP (32 bytes on x64) followed by BITMAPINFOHEADER; biHeight < 0 means top-down.
        byte* ds = stackalloc byte[104];
        if (GetObject(hbm, 104, (IntPtr)ds) == 0) return null;
        int w = *(int*)(ds + 4), h = *(int*)(ds + 8), stride = *(int*)(ds + 12);
        short bpp = *(short*)(ds + 18);
        IntPtr bits = *(IntPtr*)(ds + 24);
        int biHeight = *(int*)(ds + 40);
        if (bpp != 32 || bits == IntPtr.Zero || w <= 0 || h <= 0) return null;

        var pixels = new byte[stride * h];
        for (int row = 0; row < h; row++)
            Marshal.Copy(bits + (biHeight < 0 ? row : h - 1 - row) * stride, pixels, row * stride, stride);
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }

        var bmp = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, IntPtr buffer);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}

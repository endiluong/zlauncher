using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ZLauncher;

/// <summary>The frosted (acrylic) dark window look shared by the launcher and its dialogs.</summary>
static class Glass
{
    /// <summary>Call from OnSourceInitialized. Returns the window handle.</summary>
    public static IntPtr Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var src = HwndSource.FromHwnd(hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(KeepActiveLook);

        // No system menu so DWM doesn't paint caption buttons over the glass (minimize box stays for the taskbar).
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x80000L));

        int on = 1, round = 2, border = 0x00403C3A, backdrop = 3 /* acrylic */;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);      // dark mode
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // rounded corners
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);  // border colour
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
        return hwnd;
    }

    static IntPtr KeepActiveLook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x86 /* WM_NCACTIVATE */ && wParam == IntPtr.Zero)
        {
            handled = true; // keep the acrylic look when inactive
            return DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int idx, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}

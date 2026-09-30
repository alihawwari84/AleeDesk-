using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemoteDesk
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }

    public static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int memcmp(IntPtr a, IntPtr b, UIntPtr n);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr dst, IntPtr src, UIntPtr n);
    }

    // Injects remote mouse/keyboard input into this machine.
    public static class InputInjector
    {
        const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
        const uint KEYUP = 0x0002, EXTENDED = 0x0001;

        static readonly HashSet<int> Extended = new HashSet<int> {
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E,
            0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5
        };

        // kind: 0 move, 1/2 left down/up, 3/4 right down/up, 5/6 middle down/up, 7 wheel, 8 h-wheel.
        // x,y are 0..65535 across the primary screen.
        public static void Mouse(int kind, int x, int y, int data)
        {
            uint flags = MOVE | ABSOLUTE;
            uint md = 0;
            switch (kind)
            {
                case 1: flags |= 0x0002; break;
                case 2: flags |= 0x0004; break;
                case 3: flags |= 0x0008; break;
                case 4: flags |= 0x0010; break;
                case 5: flags |= 0x0020; break;
                case 6: flags |= 0x0040; break;
                case 7: flags |= 0x0800; md = unchecked((uint)data); break;
                case 8: flags |= 0x1000; md = unchecked((uint)data); break;
            }
            INPUT inp = new INPUT();
            inp.type = 0;
            inp.U.mi.dx = Math.Max(0, Math.Min(65535, x));
            inp.U.mi.dy = Math.Max(0, Math.Min(65535, y));
            inp.U.mi.mouseData = md;
            inp.U.mi.dwFlags = flags;
            Native.SendInput(1, new INPUT[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Key(int vk, bool down)
        {
            if (vk <= 0 || vk > 254) return;
            uint flags = down ? 0u : KEYUP;
            if (Extended.Contains(vk)) flags |= EXTENDED;
            INPUT inp = new INPUT();
            inp.type = 1;
            inp.U.ki.wVk = (ushort)vk;
            inp.U.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, 0);
            inp.U.ki.dwFlags = flags;
            Native.SendInput(1, new INPUT[] { inp }, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}

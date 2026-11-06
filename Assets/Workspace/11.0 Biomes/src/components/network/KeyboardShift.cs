using System.Runtime.InteropServices;

namespace Biomes
{
    /// <summary>
    /// Whether a Shift key is down, readable from a MIDI callback. There is no Event.current there, and in the editor the
    /// Input System's keyboard can't be relied on while an inspector has focus, so this asks the OS.
    /// </summary>
    public static class KeyboardShift
    {
#if UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
        private const int CombinedSessionState = 0;
        private const ulong ShiftFlag = 1UL << 17;

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern ulong CGEventSourceFlagsState(int stateId);

        public static bool IsHeld => (CGEventSourceFlagsState(CombinedSessionState) & ShiftFlag) != 0;
#elif UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        private const int ShiftKey = 0x10;
        private const int KeyDownBit = 0x8000;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        public static bool IsHeld => (GetAsyncKeyState(ShiftKey) & KeyDownBit) != 0;
#else
        public static bool IsHeld =>
            UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;
#endif
    }
}

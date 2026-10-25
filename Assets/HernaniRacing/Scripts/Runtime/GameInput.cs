using UnityEngine;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace HernaniRacing
{
    /// <summary>
    /// Capa de entrada compatible con el Input Manager clásico y con el nuevo Input System
    /// (si el proyecto sólo tiene activado el nuevo). Teclado + mando.
    /// </summary>
    public static class GameInput
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        static bool K(Key k) { return Keyboard.current != null && Keyboard.current[k].isPressed; }
        static bool KD(Key k) { return Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame; }
        static Gamepad Pad { get { return Gamepad.current; } }

        public static float Steer
        {
            get
            {
                float s = (K(Key.D) || K(Key.RightArrow) ? 1 : 0) - (K(Key.A) || K(Key.LeftArrow) ? 1 : 0);
                if (Pad != null && Mathf.Abs(Pad.leftStick.x.ReadValue()) > Mathf.Abs(s)) s = Pad.leftStick.x.ReadValue();
                return s;
            }
        }
        public static float Throttle
        {
            get
            {
                float t = K(Key.W) || K(Key.UpArrow) ? 1 : 0;
                if (Pad != null) t = Mathf.Max(t, Pad.rightTrigger.ReadValue());
                return t;
            }
        }
        public static float Brake
        {
            get
            {
                float b = K(Key.S) || K(Key.DownArrow) ? 1 : 0;
                if (Pad != null) b = Mathf.Max(b, Pad.leftTrigger.ReadValue());
                return b;
            }
        }
        public static bool Handbrake { get { return K(Key.Space) || (Pad != null && Pad.buttonSouth.isPressed); } }
        public static bool ResetPressed { get { return KD(Key.R) || (Pad != null && Pad.buttonNorth.wasPressedThisFrame); } }
        public static bool CameraPressed { get { return KD(Key.C) || (Pad != null && Pad.buttonWest.wasPressedThisFrame); } }
        public static bool PausePressed { get { return KD(Key.Escape) || KD(Key.P) || (Pad != null && Pad.startButton.wasPressedThisFrame); } }
        public static bool MapPressed { get { return KD(Key.Tab); } }
        public static bool MicPressed { get { return KD(Key.M); } }
        public static bool RearView { get { return K(Key.B) || (Pad != null && Pad.leftShoulder.isPressed); } }
        public static bool MapZoomPressed { get { return KD(Key.N) || (Pad != null && Pad.dpad.up.wasPressedThisFrame); } }
        public static bool ArrowsPressed { get { return KD(Key.F) || (Pad != null && Pad.dpad.down.wasPressedThisFrame); } }
        public static bool SurfacePressed { get { return KD(Key.O) || (Pad != null && Pad.dpad.left.wasPressedThisFrame); } }
        public static Vector2 Pan
        {
            get
            {
                return new Vector2((K(Key.D) || K(Key.RightArrow) ? 1 : 0) - (K(Key.A) || K(Key.LeftArrow) ? 1 : 0),
                                   (K(Key.W) || K(Key.UpArrow) ? 1 : 0) - (K(Key.S) || K(Key.DownArrow) ? 1 : 0));
            }
        }
        public static float Zoom { get { return (K(Key.E) || K(Key.PageUp) ? 1 : 0) - (K(Key.Q) || K(Key.PageDown) ? 1 : 0); } }
        public static bool Fast { get { return K(Key.LeftShift); } }
#else
        public static float Steer
        {
            get
            {
                float s = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
                float j = SafeAxis("Horizontal");
                return Mathf.Abs(j) > Mathf.Abs(s) ? j : s;
            }
        }
        public static float Throttle { get { return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.JoystickButton0) ? 1 : 0; } }
        public static float Brake { get { return Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.JoystickButton1) ? 1 : 0; } }
        public static bool Handbrake { get { return Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.JoystickButton2); } }
        public static bool ResetPressed { get { return Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.JoystickButton3); } }
        public static bool CameraPressed { get { return Input.GetKeyDown(KeyCode.C); } }
        public static bool PausePressed { get { return Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.P) || Input.GetKeyDown(KeyCode.JoystickButton7); } }
        public static bool MapPressed { get { return Input.GetKeyDown(KeyCode.Tab); } }
        public static bool MicPressed { get { return Input.GetKeyDown(KeyCode.M); } }
        public static bool RearView { get { return Input.GetKey(KeyCode.B) || Input.GetKey(KeyCode.JoystickButton4); } }
        public static bool MapZoomPressed { get { return Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.JoystickButton6); } }
        public static bool ArrowsPressed { get { return Input.GetKeyDown(KeyCode.F); } }
        public static bool SurfacePressed { get { return Input.GetKeyDown(KeyCode.O); } }
        public static Vector2 Pan
        {
            get
            {
                return new Vector2((Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                                   (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
            }
        }
        public static float Zoom { get { return (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.PageUp) ? 1 : 0) - (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.PageDown) ? 1 : 0); } }
        public static bool Fast { get { return Input.GetKey(KeyCode.LeftShift); } }

        static float SafeAxis(string name)
        {
            try { return Input.GetAxis(name); } catch { return 0f; }
        }
#endif
    }
}

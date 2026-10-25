using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Structs;
using HernaniRacing.Net;
using UnityEngine;

namespace HernaniRacing
{
    /// <summary>
    /// v16: chat de voz de la sala (todos se oyen a la vez y al mismo volumen, no por cercanía).
    /// Tecla M: abrir / cerrar el micrófono. Voz a 16 kHz mono comprimida con Opus (Concentus, C# puro) en trozos
    /// de 40 ms (~16 kbit/s): el servidor sólo la reenvía al resto de la sala.
    ///  · PC: micrófono de Unity y reproducción con un AudioClip "streaming" por jugador.
    ///  · Web: micrófono y reproducción con la Web Audio API (Plugins/WebGL/HernaniVoice.jslib); exige https.
    /// </summary>
    public class VoiceChat : MonoBehaviour
    {
        public static VoiceChat I { get; private set; }
        const int Rate = NetProto.VoiceRate, Frame = NetProto.VoiceFrame;
        const float Gate = 0.012f, GateHold = 0.35f;     // puerta de ruido: no se manda silencio

        NetSession net;
        public bool MicOn { get; private set; }
        public string MicError { get; private set; }
        public float Level { get; private set; }
        float gateUntil, toastUntil;
        string toast;

        OpusEncoder enc;
        readonly short[] pcm = new short[Frame];
        readonly byte[] packet = new byte[NetProto.VoiceMaxPacket];
        ushort seq;
        readonly float[] cap = new float[Rate];   // muestras a 16 kHz pendientes de comprimir
        int capN;

        // micrófono de PC
        AudioClip micClip;
        int micRate, micPos;
        float[] micTmp = new float[4096];
        float micFrac, micPrev;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void HRV_MicStart();
        [DllImport("__Internal")] static extern void HRV_MicStop();
        [DllImport("__Internal")] static extern int HRV_MicState();
        [DllImport("__Internal")] static extern float HRV_MicLevel();
        [DllImport("__Internal")] static extern int HRV_MicRead(float[] buf, int max);
        [DllImport("__Internal")] static extern void HRV_Play(int id, float[] buf, int count);
        readonly float[] webTmp = new float[4096];
#endif

        class Speaker
        {
            public byte id;
            public OpusDecoder dec;
            public bool hasSeq;
            public ushort lastSeq;
            public float lastHeard = -10f;
            // PC: búfer circular que lee el hilo de audio
            public readonly object sync = new object();
            public float[] ring = new float[Rate];      // 1 s
            public int rd, wr, count;
            public bool prebuffer = true;
            public AudioSource src;
        }
        readonly Dictionary<byte, Speaker> speakers = new Dictionary<byte, Speaker>();
        readonly short[] dpcm = new short[Frame * 3];
        readonly float[] dfl = new float[Frame * 3];

        public static void Attach(NetSession s)
        {
            if (I != null) return;
            var v = s.gameObject.AddComponent<VoiceChat>();
            v.net = s;
        }

        void Awake() { I = this; }

        void Start()
        {
            if (net == null) net = NetSession.I;
            if (net != null) net.Core.VoiceReceived += OnVoice;
        }

        void OnDestroy()
        {
            if (net != null) net.Core.VoiceReceived -= OnVoice;
            StopMic();
            if (I == this) I = null;
        }

        /// <summary>¿Está hablando ahora este jugador?</summary>
        public bool IsTalking(byte id)
        {
            Speaker s;
            return speakers.TryGetValue(id, out s) && Time.unscaledTime - s.lastHeard < 0.4f;
        }

        public bool MeTalking { get { return MicOn && Time.unscaledTime < gateUntil; } }

        // ------------------------------------------------------------------ micrófono
        public void Toggle()
        {
            if (MicOn) { StopMic(); Toast("Micrófono cerrado"); }
            else StartCoroutine(StartMic());
        }

        IEnumerator StartMic()
        {
            MicError = null;
            if (net == null || !net.InRoom) { Toast("El micrófono sólo funciona dentro de una sala online"); yield break; }
            if (enc == null)
            {
                enc = new OpusEncoder(Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                enc.Bitrate = 16000;
                enc.Complexity = 5;
                enc.UseInbandFEC = false;
            }
            capN = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
            HRV_MicStart();
            float t0 = Time.unscaledTime;
            while (HRV_MicState() == 1 && Time.unscaledTime - t0 < 30f) yield return null;
            int st = HRV_MicState();
            if (st != 2)
            {
                HRV_MicStop();
                MicError = st == -2 ? "No se encuentra ningún micrófono."
                    : "El navegador no deja usar el micrófono (hace falta abrir el juego por https y dar permiso).";
                Toast(MicError, 6f);
                yield break;
            }
#else
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (Microphone.devices == null || Microphone.devices.Length == 0) { MicError = "No se encuentra ningún micrófono."; Toast(MicError, 5f); yield break; }
            int min, max;
            Microphone.GetDeviceCaps(null, out min, out max);
            micRate = (min == 0 && max == 0) ? Rate : Mathf.Clamp(Rate, min, max);
            micClip = Microphone.Start(null, true, 1, micRate);
            if (micClip == null) { MicError = "No se puede abrir el micrófono."; Toast(MicError, 5f); yield break; }
            micPos = 0; micFrac = 0; micPrev = 0;
#endif
            MicOn = true;
            Toast("Micrófono ABIERTO: te oye toda la sala (M para cerrarlo)", 3f);
        }

        void StopMic()
        {
            if (!MicOn) return;
            MicOn = false; Level = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
            HRV_MicStop();
#else
            if (micClip != null) { Microphone.End(null); micClip = null; }
#endif
        }

        void Toast(string t, float secs = 2f) { toast = t; toastUntil = Time.unscaledTime + secs; }

        void Update()
        {
            if (net == null) { net = NetSession.I; if (net != null) net.Core.VoiceReceived += OnVoice; return; }
            bool canUse = net.InRoom;
            // M: micrófono (si no se está escribiendo en un campo de texto)
            if (GameInput.MicPressed && GUIUtility.keyboardControl == 0 && canUse) Toggle();
            if (MicOn && !canUse) StopMic();
            if (MicOn) Capture();
            // quien deja la sala: fuera su reproductor
            if (speakers.Count > 0 && (!canUse || Time.frameCount % 120 == 0))
            {
                List<byte> gone = null;
                foreach (var kv in speakers)
                    if (!canUse || Time.unscaledTime - kv.Value.lastHeard > 30f) (gone ?? (gone = new List<byte>())).Add(kv.Key);
                if (gone != null) foreach (var id in gone) { var s = speakers[id]; if (s.src != null) Destroy(s.src.gameObject); speakers.Remove(id); }
            }
        }

        void Capture()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            int n;
            while ((n = HRV_MicRead(webTmp, webTmp.Length)) > 0)
                for (int i = 0; i < n; i++) Push(webTmp[i]);
            Level = HRV_MicLevel();
#else
            if (micClip == null) return;
            int pos = Microphone.GetPosition(null);
            int len = micClip.samples;
            int n = (pos - micPos + len) % len;
            if (n <= 0) return;
            if (micTmp.Length < n) micTmp = new float[n];
            var tmp = n == micTmp.Length ? micTmp : new float[n];
            micClip.GetData(tmp, micPos);
            micPos = pos;
            float peak = 0;
            if (micRate == Rate)
            {
                for (int i = 0; i < n; i++) { Push(tmp[i]); peak = Mathf.Max(peak, Mathf.Abs(tmp[i])); }
            }
            else
            {
                // remuestreo lineal a 16 kHz
                float step = micRate / (float)Rate;
                float p = micFrac;
                while (p < n)
                {
                    int i0 = Mathf.FloorToInt(p);
                    float a = i0 == 0 ? micPrev : tmp[i0 - 1], b = tmp[i0], t = p - i0;
                    float v = Mathf.Lerp(a, b, t);
                    Push(v); peak = Mathf.Max(peak, Mathf.Abs(v));
                    p += step;
                }
                micFrac = p - n;
                micPrev = tmp[n - 1];
            }
            Level = Mathf.Max(peak, Level * 0.85f);
#endif
        }

        void Push(float v)
        {
            cap[capN++] = v;
            if (capN < Frame) return;
            capN = 0;
            float peak = 0;
            for (int i = 0; i < Frame; i++)
            {
                float x = Mathf.Clamp(cap[i], -1f, 1f);
                peak = Mathf.Max(peak, Mathf.Abs(x));
                pcm[i] = (short)(x * 32767f);
            }
            if (peak > Gate) gateUntil = Time.unscaledTime + GateHold;
            if (Time.unscaledTime > gateUntil) return;            // silencio: no se manda nada
            int len;
            try { len = enc.Encode(pcm, 0, Frame, packet, 2, packet.Length - 2); }
            catch (System.Exception) { return; }
            if (len <= 0) return;
            seq++;
            packet[0] = (byte)(seq & 0xFF); packet[1] = (byte)(seq >> 8);
            net.Core.SendVoice(packet, len + 2);
        }

        // ------------------------------------------------------------------ voz recibida
        void OnVoice(byte from, byte[] data, int offset, int count)
        {
            if (count < 3 || from == net.MyId) return;
            Speaker s;
            if (!speakers.TryGetValue(from, out s))
            {
                s = new Speaker { id = from, dec = new OpusDecoder(Rate, 1) };
                speakers[from] = s;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
                var go = new GameObject("Voz_" + from);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.spatialBlend = 0f; src.volume = 1f; src.loop = true; src.playOnAwake = false;
                src.ignoreListenerPause = true; src.ignoreListenerVolume = false;
                var sp = s;
                src.clip = AudioClip.Create("voz_" + from, Rate * 2, 1, Rate, true, data2 => Fill(sp, data2));
                src.Play();
                s.src = src;
#endif
            }
            ushort sq = (ushort)(data[offset] | (data[offset + 1] << 8));
            int lost = 0;
            if (s.hasSeq)
            {
                int d = (ushort)(sq - s.lastSeq);
                if (d == 0 || d > 30000) return;                   // repetido o atrasado
                lost = Mathf.Min(d - 1, 2);
                // tras un silencio (puerta de ruido) no se "rellena" nada
                if (Time.unscaledTime - s.lastHeard > 0.2f) lost = 0;
            }
            s.hasSeq = true; s.lastSeq = sq; s.lastHeard = Time.unscaledTime;
            int n = 0;
            try
            {
                for (int k = 0; k < lost; k++) n += s.dec.Decode(null, 0, 0, dpcm, n, Frame, false);   // disimula lo perdido
                n += s.dec.Decode(data, offset + 2, count - 2, dpcm, n, Frame, false);
            }
            catch (System.Exception) { return; }
            for (int i = 0; i < n; i++) dfl[i] = dpcm[i] / 32768f;
#if UNITY_WEBGL && !UNITY_EDITOR
            HRV_Play(from, dfl, n);
#else
            lock (s.sync)
            {
                for (int i = 0; i < n; i++)
                {
                    s.ring[s.wr] = dfl[i]; s.wr = (s.wr + 1) % s.ring.Length;
                    if (s.count < s.ring.Length) s.count++; else s.rd = (s.rd + 1) % s.ring.Length;
                }
                // retraso máximo ~0,5 s: se tira lo más viejo
                int maxKeep = Rate / 2;
                if (s.count > maxKeep) { int drop = s.count - maxKeep; s.rd = (s.rd + drop) % s.ring.Length; s.count = maxKeep; }
            }
#endif
        }

        /// <summary>Hilo de audio (PC): saca la voz del búfer; con un pequeño colchón de 80 ms contra los saltos de la red.</summary>
        static void Fill(Speaker s, float[] data)
        {
            lock (s.sync)
            {
                if (s.prebuffer && s.count >= Rate * 8 / 100) s.prebuffer = false;
                int i = 0;
                if (!s.prebuffer)
                {
                    for (; i < data.Length && s.count > 0; i++) { data[i] = s.ring[s.rd]; s.rd = (s.rd + 1) % s.ring.Length; s.count--; }
                    if (s.count == 0) s.prebuffer = true;
                }
                for (; i < data.Length; i++) data[i] = 0f;
            }
        }

        // ------------------------------------------------------------------ indicador
        GUIStyle st;
        void OnGUI()
        {
            if (net == null || !net.InRoom) return;
            GUI.depth = -20;
            if (st == null)
            {
                st = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
                st.normal.textColor = Color.white;
            }
            float ui = Screen.height / 1080f;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(ui, ui, 1));
            float H = 1080f, x = 20f, y = H - 82f;
            // quién habla (de abajo arriba)
            if (net.Room != null)
                foreach (var p in net.Room.players)
                {
                    if (p.id == net.MyId || !IsTalking(p.id)) continue;
                    Badge(new Rect(x, y, 260, 30), new Color(0.1f, 0.5f, 0.2f, 0.85f), "  ) ) )  " + p.profile.nick);
                    y -= 34f;
                }
            // mi micrófono
            if (MicOn)
            {
                var r = new Rect(x, y, 260, 30);
                Badge(r, MeTalking ? new Color(0.8f, 0.1f, 0.1f, 0.9f) : new Color(0.45f, 0.08f, 0.08f, 0.85f), "  MICRO ABIERTO (M)");
                GUI.DrawTexture(new Rect(r.xMax - 64, r.y + 11, 54, 8), Tex.Solid(new Color(0, 0, 0, 0.5f)));
                GUI.DrawTexture(new Rect(r.xMax - 64, r.y + 11, 54 * Mathf.Clamp01(Level * 3f), 8), Tex.Solid(new Color(0.3f, 1f, 0.4f)));
                y -= 34f;
            }
            if (Time.unscaledTime < toastUntil && !string.IsNullOrEmpty(toast))
                Badge(new Rect(x, y, Mathf.Min(900f, 30f + toast.Length * 10f), 30), new Color(0.05f, 0.06f, 0.08f, 0.85f), "  " + toast);
            GUI.matrix = old;
        }

        void Badge(Rect r, Color bg, string text)
        {
            GUI.DrawTexture(r, Tex.Solid(bg));
            GUI.Label(new Rect(r.x + 4, r.y + 2, r.width - 8, r.height - 4), text, st);
        }
    }
}

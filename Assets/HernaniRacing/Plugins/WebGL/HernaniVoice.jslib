// Hernani Racing: chat de voz en la versión web.
// Unity 6.3 no tiene micrófono en la web y no puede reproducir audio generado por código, así que aquí se usa
// la Web Audio API directamente: el micrófono se reduce a 16 kHz mono y el C# lo recoge en cada fotograma
// (lo comprime con Opus); la voz recibida (ya descomprimida en C#) se programa aquí para que suene seguida.
// El navegador sólo deja usar el micrófono en páginas https (o en http://localhost).
var HernaniVoice = {
  $HRV: { ctx: null, stream: null, src: null, proc: null, ring: null, rd: 0, wr: 0, state: 0, level: 0,
          next: {}, gain: null, frac: 0, last: 0 },

  $HRV_Ctx: function () {
    if (!HRV.ctx) {
      var AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return null;
      HRV.ctx = new AC();
      HRV.gain = HRV.ctx.createGain();
      HRV.gain.gain.value = 1.0;
      HRV.gain.connect(HRV.ctx.destination);
    }
    if (HRV.ctx.state === "suspended") { try { HRV.ctx.resume(); } catch (e) {} }
    return HRV.ctx;
  },

  // 0 apagado, 1 pidiendo permiso, 2 grabando, -1 sin permiso / sin https, -2 no hay micrófono
  HRV_MicStart: function () {
    if (HRV.state === 1 || HRV.state === 2) return;
    if (!window.isSecureContext || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { HRV.state = -1; return; }
    var ctx = HRV_Ctx();
    if (!ctx) { HRV.state = -2; return; }
    HRV.state = 1;
    HRV.ring = new Float32Array(16000 * 2); HRV.rd = 0; HRV.wr = 0; HRV.frac = 0; HRV.last = 0;
    navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 } })
      .then(function (stream) {
        if (HRV.state !== 1) { stream.getTracks().forEach(function (t) { t.stop(); }); return; }
        HRV.stream = stream;
        HRV.src = ctx.createMediaStreamSource(stream);
        HRV.proc = ctx.createScriptProcessor(2048, 1, 1);
        var step = ctx.sampleRate / 16000;
        HRV.proc.onaudioprocess = function (ev) {
          var x = ev.inputBuffer.getChannelData(0);
          var o = ev.outputBuffer.getChannelData(0);
          for (var i = 0; i < o.length; i++) o[i] = 0;          // nada al altavoz
          if (HRV.state !== 2) return;
          var ring = HRV.ring, n = ring.length, peak = 0;
          // remuestreo a 16 kHz (interpolación lineal; el micro ya viene filtrado por el navegador)
          var pos = HRV.frac;
          while (pos < x.length - 1) {
            var i0 = Math.floor(pos), t = pos - i0;
            var v = x[i0] * (1 - t) + x[i0 + 1] * t;
            ring[HRV.wr] = v; HRV.wr = (HRV.wr + 1) % n;
            if (HRV.wr === HRV.rd) HRV.rd = (HRV.rd + 1) % n;     // lleno: se pierde lo más viejo
            var a = v < 0 ? -v : v; if (a > peak) peak = a;
            pos += step;
          }
          HRV.frac = pos - (x.length - 1);
          HRV.level = Math.max(peak, HRV.level * 0.85);
        };
        HRV.src.connect(HRV.proc);
        HRV.proc.connect(ctx.destination);
        HRV.state = 2;
      })
      .catch(function (e) { HRV.state = (e && e.name === "NotFoundError") ? -2 : -1; });
  },

  HRV_MicStop: function () {
    HRV.state = 0; HRV.level = 0;
    try { if (HRV.proc) { HRV.proc.disconnect(); HRV.proc.onaudioprocess = null; } } catch (e) {}
    try { if (HRV.src) HRV.src.disconnect(); } catch (e) {}
    try { if (HRV.stream) HRV.stream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) {}
    HRV.proc = null; HRV.src = null; HRV.stream = null;
  },

  HRV_MicState: function () { return HRV.state; },
  HRV_MicLevel: function () { return HRV.level; },

  // copia hasta 'max' muestras (float, 16 kHz) al búfer del C#; devuelve cuántas
  HRV_MicRead: function (ptr, max) {
    if (HRV.state !== 2 || !HRV.ring) return 0;
    var n = HRV.ring.length, c = 0, base = ptr >> 2;
    while (HRV.rd !== HRV.wr && c < max) { HEAPF32[base + c] = HRV.ring[HRV.rd]; HRV.rd = (HRV.rd + 1) % n; c++; }
    return c;
  },

  // reproduce 'count' muestras (float, 16 kHz) de la voz del jugador 'id', seguidas de lo anterior
  HRV_Play: function (id, ptr, count) {
    var ctx = HRV_Ctx();
    if (!ctx || count <= 0) return;
    var buf = ctx.createBuffer(1, count, 16000);
    buf.getChannelData(0).set(HEAPF32.subarray(ptr >> 2, (ptr >> 2) + count));
    var s = ctx.createBufferSource();
    s.buffer = buf;
    s.connect(HRV.gain);
    var now = ctx.currentTime, t = HRV.next[id] || 0;
    if (t < now + 0.02 || t > now + 0.6) t = now + 0.08;      // primer trozo o desfase: pequeño colchón
    try { s.start(t); } catch (e) { return; }
    HRV.next[id] = t + count / 16000;
  },

  HRV_Volume: function (v) { HRV_Ctx(); if (HRV.gain) HRV.gain.gain.value = v; }
};
// dependencias explícitas (como en HernaniWebSocket.jslib)
HernaniVoice['$HRV_Ctx__deps'] = ['$HRV'];
['HRV_MicStart', 'HRV_MicStop', 'HRV_MicState', 'HRV_MicLevel', 'HRV_MicRead', 'HRV_Play', 'HRV_Volume'].forEach(function (f) {
  HernaniVoice[f + '__deps'] = ['$HRV', '$HRV_Ctx'];
});
mergeInto(LibraryManager.library, HernaniVoice);

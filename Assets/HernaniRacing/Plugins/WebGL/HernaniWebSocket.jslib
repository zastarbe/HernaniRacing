// Hernani Racing: WebSocket para la versión web (el navegador no permite UDP).
// El C# consulta el estado y recoge los mensajes en cada fotograma (sin callbacks).
var HernaniWebSocket = {
  $HRWS: { ws: null, queue: [], state: 0, closeCode: 0, closeReason: "" },

  HRWS_Connect: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    if (HRWS.ws) { var old = HRWS.ws; HRWS.ws = null; try { old.close(1000); } catch (e) {} }
    HRWS.queue = []; HRWS.state = 1; HRWS.closeCode = 0; HRWS.closeReason = "";
    var ws;
    try { ws = new WebSocket(url); }
    catch (e) { HRWS.state = 3; HRWS.closeCode = 1015; HRWS.closeReason = "" + e; return; }
    ws.binaryType = "arraybuffer";
    HRWS.ws = ws;
    ws.onopen = function () { if (HRWS.ws === ws) HRWS.state = 2; };
    ws.onmessage = function (ev) {
      if (HRWS.ws !== ws || !(ev.data instanceof ArrayBuffer)) return;
      var u = new Uint8Array(ev.data);
      if (u.length === 0) return;
      // ping del servidor: se devuelve en el acto (aunque la pestaña esté en segundo plano)
      if (u[0] === 0xFE) { try { ws.send(u); } catch (e) {} return; }
      // pestaña en segundo plano: el juego no recoge mensajes; no acumular posiciones viejas
      if (HRWS.queue.length > 3000 && u[0] === 105) return;
      HRWS.queue.push(u);
    };
    ws.onclose = function (ev) {
      if (HRWS.ws !== ws) return;
      HRWS.state = 3; HRWS.closeCode = ev.code || 1006; HRWS.closeReason = ev.reason || "";
    };
    ws.onerror = function () { };
  },

  HRWS_State: function () { return HRWS.state; },
  HRWS_CloseCode: function () { return HRWS.closeCode; },

  HRWS_Send: function (ptr, len) {
    var ws = HRWS.ws;
    if (!ws || HRWS.state !== 2) return 0;
    // conexión muy lenta: no amontonar posiciones (sí los mensajes importantes)
    if (ws.bufferedAmount > 262144 && HEAPU8[ptr] === 12) return 0;
    try { ws.send(HEAPU8.slice(ptr, ptr + len)); return 1; } catch (e) { return 0; }
  },

  HRWS_Recv: function (ptr, max) {
    if (HRWS.queue.length === 0) return -1;
    var u = HRWS.queue[0];
    if (u.length > max) return -2 - u.length;   // no cabe: el C# agranda el búfer y vuelve a pedir
    HRWS.queue.shift();
    HEAPU8.set(u, ptr);
    return u.length;
  },

  HRWS_Close: function () {
    var ws = HRWS.ws;
    HRWS.ws = null; HRWS.state = 0; HRWS.queue = [];
    if (ws) { try { ws.close(1000); } catch (e) {} }
  }
};
autoAddDeps(HernaniWebSocket, '$HRWS');
mergeInto(LibraryManager.library, HernaniWebSocket);

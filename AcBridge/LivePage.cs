namespace AcBridge;

/// <summary>Minimale Testseite: verbindet sich per WebSocket und zeigt die Rohdaten live an.</summary>
public static class LivePage
{
    public const string Html = """
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AC Bridge Live</title>
<style>
  body { font-family: system-ui, sans-serif; background: #111; color: #eee; margin: 16px; }
  .row { display: flex; gap: 24px; flex-wrap: wrap; margin-bottom: 16px; }
  .big { font-size: 48px; font-weight: 700; font-variant-numeric: tabular-nums; }
  .lbl { font-size: 12px; color: #999; text-transform: uppercase; }
  .bar { width: 200px; height: 14px; background: #333; border-radius: 4px; overflow: hidden; }
  .bar > div { height: 100%; }
  #state { font-size: 13px; color: #999; }
  pre { background: #1c1c1c; padding: 12px; border-radius: 6px; font-size: 12px; overflow: auto; max-height: 60vh; }
</style>
</head>
<body>
  <div id="state">verbinde…</div>
  <div class="row">
    <div><div class="lbl">km/h</div><div class="big" id="speed">–</div></div>
    <div><div class="lbl">Gang</div><div class="big" id="gear">–</div></div>
    <div><div class="lbl">RPM</div><div class="big" id="rpm">–</div></div>
    <div><div class="lbl">Runde</div><div class="big" id="lap">–</div></div>
  </div>
  <div class="row">
    <div><div class="lbl">Gas</div><div class="bar"><div id="gas" style="background:#3c3"></div></div></div>
    <div><div class="lbl">Bremse</div><div class="bar"><div id="brake" style="background:#e33"></div></div></div>
  </div>
  <div class="lbl">Session</div><pre id="session">–</pre>
  <div class="lbl">Rohdaten (letzter Frame)</div><pre id="raw">–</pre>
<script>
  const $ = id => document.getElementById(id);
  const fmt = ms => ms > 0 ? `${Math.floor(ms / 60000)}:${((ms % 60000) / 1000).toFixed(3).padStart(6, "0")}` : "–";
  function connect() {
    const ws = new WebSocket(`ws://${location.host}/ws/telemetry?hz=20`);
    ws.onopen = () => $("state").textContent = "WebSocket verbunden";
    ws.onclose = () => { $("state").textContent = "getrennt, neuer Versuch…"; setTimeout(connect, 1000); };
    ws.onmessage = e => {
      const m = JSON.parse(e.data);
      if (m.type === "status") $("state").textContent =
        `Quelle: ${m.data.source} · AC ${m.data.connected ? "verbunden" : "nicht verbunden (auf die Strecke fahren)"}`;
      if (m.type === "session") $("session").textContent = JSON.stringify(m.data, null, 2);
      if (m.type === "telemetry") {
        const d = m.data;
        $("speed").textContent = d.speedKmh.toFixed(0);
        $("gear").textContent = d.gear === -1 ? "R" : d.gear === 0 ? "N" : d.gear;
        $("rpm").textContent = d.rpm;
        $("lap").textContent = fmt(d.lap.currentLapMs);
        $("gas").style.width = (d.throttle * 100) + "%";
        $("brake").style.width = (d.brake * 100) + "%";
        $("raw").textContent = JSON.stringify(d, null, 2);
      }
    };
  }
  connect();
</script>
</body>
</html>
""";
}

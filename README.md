# AC Bridge

Kleiner .NET-10-Dienst, der die Telemetrie von **Assetto Corsa** (Original, nicht ACC) aus dem Shared Memory liest und per **WebSocket** (plus ein paar REST-Endpoints) ins LAN streamt, z. B. an eine SwiftUI-App auf iPad oder Mac.

```
AC (Windows) ──Shared Memory──▶ AcBridge ──ws://<pc-ip>:5080/ws/telemetry──▶ iPad / Mac / Angular
```

## Starten

```bash
dotnet run                                  # Windows: liest AC, sonst automatisch Mock-Daten
dotnet run -- --Telemetry:Source=Mock       # Fake-Daten erzwingen (auch unter Windows)
```

Unter macOS/Linux läuft automatisch der **Mock**. Damit kannst du das Frontend bauen, ohne dass AC läuft.

Einmalig unter Windows den Port freigeben (Admin-Konsole), sonst kommt das iPad nicht durch:

```powershell
netsh advfirewall firewall add rule name="AC Bridge" dir=in action=allow protocol=TCP localport=5080
```

Konfiguration in `appsettings.json`:

| Key | Default | |
|---|---|---|
| `Telemetry:Source` | `Auto` | `Auto` / `SharedMemory` / `Mock` |
| `Telemetry:PollHz` | `60` | Wie oft das Shared Memory gelesen wird |
| `Telemetry:DefaultStreamHz` | `30` | Rate pro Client, wenn kein `?hz=` angegeben ist |
| `Telemetry:MaxStreamHz` | `60` | Obergrenze |
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:5080` | Port/Interface |

## Endpoints

| | Pfad | Antwort |
|---|---|---|
| WS | `/ws/telemetry?hz=30` | Stream, siehe unten |
| GET | `/api/status` | `BridgeStatus` |
| GET | `/api/session` | `SessionInfo` (204, wenn keine Session) |
| GET | `/api/telemetry` | letzter `TelemetryFrame` (204, wenn keiner) |

### WebSocket-Protokoll

Alle Nachrichten sind JSON-Text-Frames in einem Umschlag `{ "type": "...", "data": ... }`:

| type | wann | data |
|---|---|---|
| `status` | beim Verbinden, wenn AC verbunden/getrennt wird | `BridgeStatus` |
| `session` | beim Verbinden, bei Auto-/Streckenwechsel | `SessionInfo` oder `null` |
| `telemetry` | mit der gewählten Rate, nur wenn es neue Daten gibt | `TelemetryFrame` |

Client → Server: `{ "hz": 10 }` ändert die Rate zur Laufzeit (1 bis `MaxStreamHz`).

Die Felder stehen in `Models/Dtos.cs`. Ein paar Konventionen:
- Räder sind immer `{ fl, fr, rl, rr }`
- `gear`: `-1` = R, `0` = N, `1…` = Gang
- Zeiten in ms, Temperaturen in °C, Reifendruck in psi, Sprit in Liter
- `lap.trackPosition` geht von 0 bis 1 entlang der Ideallinie, `carCoordinates` sind Weltkoordinaten [x, y, z]

Ein Frame hat ca. 1,5 KB, bei 30 Hz also rund 45 KB/s. Das schafft jedes WLAN.

## Swift-Client (Skizze)

```swift
struct Envelope<T: Decodable>: Decodable { let type: String; let data: T }
struct TypeOnly: Decodable { let type: String }

struct Wheels: Decodable { let fl, fr, rl, rr: Double }
struct Frame: Decodable {
    let speedKmh: Double; let rpm: Int; let gear: Int
    let throttle: Double; let brake: Double; let fuel: Double
    // … nach Bedarf aus Dtos.cs ergänzen
}

@Observable final class TelemetryClient {
    var frame: Frame?
    private var task: URLSessionWebSocketTask?

    func connect(host: String) {
        task = URLSession.shared.webSocketTask(with: URL(string: "ws://\(host):5080/ws/telemetry?hz=30")!)
        task?.resume()
        receive()
    }

    private func receive() {
        task?.receive { [weak self] result in
            guard let self else { return }
            if case .success(.string(let text)) = result {
                let data = Data(text.utf8)
                if (try? JSONDecoder().decode(TypeOnly.self, from: data))?.type == "telemetry",
                   let msg = try? JSONDecoder().decode(Envelope<Frame>.self, from: data) {
                    Task { @MainActor in self.frame = msg.data }
                }
            }
            if case .failure = result { return } // hier reconnecten
            self.receive()
        }
    }
}
```

**Info.plist** (sonst blockt iOS/macOS die unverschlüsselte Verbindung im LAN):
- `NSAppTransportSecurity` → `NSAllowsLocalNetworking` = `YES`
- `NSLocalNetworkUsageDescription` = z. B. „Verbindet sich mit der AC Bridge auf deinem PC"
- macOS-App mit Sandbox: zusätzlich die Capability *Outgoing Connections (Client)*

## Aufbau

```
Program.cs                       DI, Endpoints
Telemetry/AcStructs.cs           Shared-Memory-Layouts (Physics, Graphics, Static)
Telemetry/Sources.cs             AcSharedMemorySource (Windows) + MockTelemetrySource
Telemetry/TelemetryService.cs    Store (letzter Stand) + Poller (BackgroundService)
Telemetry/TelemetryWebSocket.cs  Pro Client eine Sende-Schleife mit eigener Rate
Telemetry/AcMapper.cs            Structs → DTOs
Models/Dtos.cs                   JSON-Modelle
```

Der Poller liest mit `PollHz` und legt nur den **letzten** Frame im Store ab. Jeder WebSocket-Client liest in seinem eigenen Takt daraus. Ein langsamer Client staut sich also nicht auf und bremst keinen anderen aus.

## Hinweise

- Die Structs decken den vorderen Teil der offiziellen Shared-Memory-Referenz ab. Neuere Felder hängt AC hinten an, die stören nicht. Wenn du mehr brauchst (z. B. `P2PActivations`), einfach hinten in `AcStructs.cs` ergänzen.
- Pausiert AC oder bist du im Menü, kommen keine neuen Pakete. Dann sendet die Bridge auch keine `telemetry`-Nachrichten. Den Zustand siehst du im Frame-Feld `status` (`off`/`replay`/`live`/`pause`).
- Für mehrere Autos (Gegner-Positionen) reicht das Shared Memory nicht. Dafür bräuchtest du eine Python-App in AC oder das UDP-Interface.

## Minisektoren: Solo- und Feld-Modus

Die Bridge entscheidet automatisch:

| Modus | wann | lila | grün | gelb |
|---|---|---|---|---|
| `solo` | allein auf der Strecke / keine In-Game-App | eigener Bestwert | schneller als eigene beste Runde | langsamer |
| `field` | andere Autos fahren **und** die In-Game-App „AcBridge" ist aktiv | Feld-Bestwert | eigener Bestwert | langsamer |

`neutral` = gefahren, aber noch kein Vergleichswert. Im JSON: `miniSectors.mode` und `miniSectors.fieldCars`.

Für den Feld-Modus braucht AC die Python-App aus `AcBridgeApp/apps/python/AcBridge` (siehe dortiges README).
Sie schreibt die Daten aller Autos ins Shared Memory `Local\acbridge_cars`. Im Mock-Modus fahren zwei simulierte KI-Autos mit.

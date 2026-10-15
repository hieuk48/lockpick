# Lockpick API

Lockpick can be driven and read by other programs: your own scripts or LLM tools, OBS overlays, LiveSplit, a Stream
Deck or other macro tools, or a device on your network such as a phone or a Steam Deck. The API is **off** until you
turn it on in **Settings → API for other tools**.

- **Address:** `http://127.0.0.1:1996/api/v1` (the port is configurable). It listens on this PC only, unless you tick
  *Allow other devices on my network*; then use the PC's network address. Windows may ask to let Lockpick through the
  firewall.
- **Token:** every request needs the token shown in Settings. Use one of:
  - `Authorization: Bearer <token>`
  - `X-Lockpick-Token: <token>`
  - `?token=<token>` (for browser sources and tools that can't set headers)

  *New token* replaces it; programs using the old one stop working.
- **Trainer commands** need a second switch, *Allow trainer commands*, and work only while Trainer Mode is on. They
  go through the same checks as the app's own buttons: bounds such as HP up to max HP, read back, and logged with
  source `api`. In Challenge Mode they are refused.
- Responses are JSON (camelCase). A command that couldn't be done answers 409 with `error` explaining why. A missing
  or wrong token answers 401, and trainer commands that aren't allowed answer 403.

## Reading

| Request | What you get |
|---|---|
| `GET /api/v1` | Name, version, list of endpoints |
| `GET /api/v1/status` | Connection (`attached`, `searching`, …), game build, trainer mode and write access, capture running, API settings |
| `GET /api/v1/state` | The live game state: `inGame`, `character`, `health {current, max}`, `room {id, stage, name}`, `camera`, `position {x, y, z}`, `gameTimeSeconds`, `inventory [{slot, itemId, name, quantity}]`, `itemBox`, `trainer` |
| `GET /api/v1/events?after=N` | Events after sequence number `N` (`{events: [...], last}`); poll with the last number you saw |
| `GET /api/v1/events/stream?after=N` | The same as a live [server-sent event](https://html.spec.whatwg.org/multipage/server-sent-events.html) stream: each message has `id` (sequence), `event` (type) and `data` (JSON); a comment every 15 s keeps it open |
| `GET /api/v1/trainer` | Trainer mode, write access, whether this run was changed, locks, infinite ammo, the last 50 actions |

Event types: `GameStarted`, `GameStopped`, `GameLoaded`, `GameSaved`, `RoomChanged`, `CameraChanged`,
`PlayerHealthChanged`, `PlayerDamaged`, `TrainerActionExecuted`, `RunInvalidated`.

## Commands (POST)

| Request | Body | Does |
|---|---|---|
| `/api/v1/capture/start` | — | Starts a capture (as Debug → Start capture) |
| `/api/v1/capture/marker` | `{"note": "split 1"}` (optional) | Marks the moment in the running capture; returns the marker number |
| `/api/v1/capture/stop` | — | Stops and saves the capture; returns the file |
| `/api/v1/trainer/mode` | `{"mode": "readOnly" \| "trainer" \| "challenge"}` | Switching to read-only or Challenge is always allowed; to `trainer` only with trainer commands allowed |
| `/api/v1/trainer/health` | `{"value": 90}` or `{"full": true}`, optional `"lock": true` | Sets (or locks) HP |
| `/api/v1/trainer/health/unlock` | — | Releases the HP lock |
| `/api/v1/trainer/ammo` | `{"enabled": true}` | Infinite ammo on / off |
| `/api/v1/trainer/inventory/quantity` | `{"slot": 0, "quantity": 99}` | Changes the count of a held weapon (loaded rounds), ammo or ink ribbons; `slot` as in `state.inventory` |
| `/api/v1/trainer/itembox` | `{"itemId": 12, "quantity": 30}` | Puts an item in the first empty item-box entry; the player takes it out at any item box (`GET /api/v1/trainer/items` lists the ids) |

## Examples

```bash
TOKEN=...   # from Settings
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:1996/api/v1/state
curl -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"note":"Yawn 1"}' http://127.0.0.1:1996/api/v1/capture/marker
curl -N -H "Authorization: Bearer $TOKEN" http://127.0.0.1:1996/api/v1/events/stream
```

```powershell
$h = @{ Authorization = "Bearer $env:LOCKPICK_TOKEN" }
Invoke-RestMethod http://127.0.0.1:1996/api/v1/state -Headers $h
Invoke-RestMethod http://127.0.0.1:1996/api/v1/trainer/health -Method Post -Headers $h `
  -ContentType application/json -Body '{"full":true}'
```

```python
import json, urllib.request
req = urllib.request.Request("http://127.0.0.1:1996/api/v1/state", headers={"Authorization": f"Bearer {TOKEN}"})
print(json.load(urllib.request.urlopen(req))["health"])
```

**Stream Deck and macro tools:** use an "HTTP request" or "website" action. Set it to POST to a command URL and add
the token as a header, or as `?token=` if the tool can't set headers.

**OBS:** a browser source can open `events/stream?token=...` with `EventSource`. A ready-made overlay page is planned
for 0.3.0.

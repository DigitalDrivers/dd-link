# Message contract between dd-link and the platform

The plugin sends one message per finished session (practice, qualifying, race) in which at least one
driver took part. Booking sessions and empty sessions are not reported.

## Transport

`POST {Endpoint}` with `Content-Type: application/json` and two headers:

| Header | Value |
| --- | --- |
| `X-DD-Timestamp` | Unix time in seconds at the moment of sending |
| `X-DD-Signature` | `v1=` + lowercase hex of `HMAC-SHA256(secret, "{timestamp}.{raw body}")` |

The platform must verify the signature over the raw body bytes, reject timestamps that are too old, and
deduplicate by `id`.

| Response | Meaning for the plugin |
| --- | --- |
| `2xx` | Accepted; the message is removed from the spool |
| `409` | Already known (duplicate `id`); treated as accepted |
| `408`, `429`, `5xx`, network error | Kept; retried after 5 s, 15 s, 60 s, then every 5 min, also after a server restart |
| other `4xx` | Rejected for good; the file moves to `<spool>/failed/` and is not sent again |

Delivery is at-least-once and in order: while one message cannot be delivered, later ones wait.

## Body

```json
{
  "id": "0b5c2d0e-6a63-4a7e-9d0b-0a4e4f7a1c11",
  "type": "session.completed",
  "eventId": "evt_123",
  "serverId": "race-1",
  "sentAt": "2026-09-18T19:42:10.123+00:00",
  "session": {
    "kind": "race",
    "name": "Race",
    "track": "ks_nurburgring",
    "trackLayout": "layout_gp_a",
    "laps": 8,
    "timeMinutes": 0,
    "durationMs": 912345
  },
  "classification": [
    {
      "position": 1,
      "status": "classified",
      "steamId": "76561198000000001",
      "name": "Driver 1",
      "carModel": "ks_porsche_911_gt3_r_2016",
      "skin": "racing_17",
      "laps": 8,
      "totalTimeMs": 900100,
      "bestLapMs": 110500,
      "gridPosition": 2,
      "crew": [
        { "steamId": "76561198000000001", "name": "Driver 1", "laps": 8 }
      ]
    }
  ],
  "laps": [
    { "steamId": "76561198000000001", "lapNumber": 1, "lapTimeMs": 112300, "cuts": 0, "sessionTimeMs": 118000 }
  ],
  "collisions": [
    { "steamId": "76561198000000001", "otherSteamId": null, "speedKmh": 42.5, "x": 1.0, "y": 2.0, "z": 3.0, "sessionTimeMs": 60000 }
  ],
  "connections": [
    { "steamId": "76561198000000002", "name": "Driver 2", "connected": false, "sessionTimeMs": 400000 }
  ]
}
```

- `kind`: `practice`, `qualifying` or `race`.
- SteamIDs are strings because they exceed JavaScript's safe integer range.
- `otherSteamId` is `null` for contact with the environment. A car-to-car contact is usually reported by
  both cars, each from its own point of view.
- `bestLapMs` is `null` when the driver set no lap time.
- `gridPosition` is the starting position among the drivers in the message (1 = pole). It is `null` outside races.
- `crew` lists everyone who drove the car in the session, in the order of their first stint, with the laps
  each of them completed. Without a driver swap it is the driver alone. `steamId` and `name` of the entry are
  the driver who had the car last; `laps`, `totalTimeMs` and `bestLapMs` belong to the car.

## Live state

With `LiveEndpoint` configured, the plugin posts the state of the running session about once a second
(`LiveIntervalMilliseconds`, default 1000), signed like every other message. It is meant for live timing and a
track map. Nothing is stored or retried: a message that cannot be delivered is dropped, the next one carries
the current state anyway. Only connected cars that have sent a position are listed, in live order.

```json
{
  "type": "live.state",
  "eventId": "evt_123",
  "serverId": "race-1",
  "sentAt": "2026-09-18T19:42:10.123+00:00",
  "session": { "kind": "race", "name": "Race", "track": "ks_nurburgring", "trackLayout": "layout_gp_a",
               "laps": 8, "timeMinutes": 0, "elapsedMs": 412000, "timeLeftMs": 0 },
  "cars": [
    { "carId": 0, "steamId": "76561198000000001", "name": "Driver 1", "carModel": "ks_porsche_911_gt3_r_2016",
      "position": 1, "laps": 3, "totalTimeMs": 341200, "bestLapMs": 110500, "lastLapMs": 111050, "finished": false,
      "spline": 0.4312, "x": 12.5, "z": -40.25, "speedKmh": 182, "gear": 4, "rpm": 7200, "gas": 87,
      "sectors": [35100], "telemetry": null }
  ],
  "spectators": [
    { "steamId": "76561198000000005", "name": "Steward", "inPitLane": true }
  ]
}
```

- `position` is the live rank. Race: more laps, then (once finished) the earlier finish, then the progress along
  the lap (`spline`, 0 to 1). Practice and qualifying: the best lap.
- `totalTimeMs` is the race clock when the car last crossed the line; gaps between cars on the same lap are the
  difference of these values.
- `x` and `z` are the world position on the ground plane in metres, `gear` is -1 for reverse and 0 for neutral,
  `gas` is the throttle in percent. Brake, fuel and tyre state are not known to the server itself; see `telemetry`.
- `sectors` are the sector times of the lap in progress, as far as they are set.
- `telemetry` is what only the driver's game knows, or `null` until that game has reported (it needs Custom
  Shaders Patch): `fuelLitres`, `maxFuelLitres`, `fuelPerLapLitres` (0 until the game has an estimate),
  `engineLife` (1000 new, 0 broken), `brake` (0 to 1), `tyreWear` (0 new, 1 worn out), `tyreTemperature` (core, °C),
  `tyrePressure` (psi), each for front left, front right, rear left, rear right, `damage` for front, rear, left,
  right (highest collision speed in km/h taken there) and `inPitLane`. The plugin ships `lua/telemetry.lua` to
  every game on the server; it reports once a second. The server does not pass these messages on to other
  drivers. The platform must show them to the car's own team only.

## Spectator slots

A slot of the entry list with `SPECTATOR_MODE=1` is for watching from inside the game: a driver parks there and
follows the race with the game's cameras (Custom Shaders Patch tells the server whose surroundings to send).
To Assetto Corsa it is an ordinary car, so give these slots a car model of their own: the server hands a
driver the matching slot with the most allowed SteamIDs, which would be the spectator slot if it shared the
model of the race cars. The plugin treats such a slot as no part of the race:

- it appears in no classification and reports no laps, contacts or connections;
- the live state lists its driver under `spectators` (`steamId`, `name`, `inPitLane` from the driver's game,
  `null` until that has reported) instead of `cars`;
- in qualifying and races the driver is sent back to the pit box, five seconds into the session and again
  every ten seconds while the game reports the car outside the pit lane. The game puts every connected car
  on the grid when a race starts, and a parked car there is an obstacle.

A connected spectator counts for the server: a race with a single car survives a driver swap while somebody
watches. (The plugin reads the slots from the entry list, because the server clears its own copy of the flag
whenever a driver takes the slot.)

## Driver swaps

Assetto Corsa has no driver swap. A slot of the entry list can name several SteamIDs (`GUID=a;b;c`); the swap
is one driver leaving the server and a crew-mate joining the same slot. The server starts the slot's result
from zero when a different driver joins, so the plugin remembers what the car had achieved (at every
completed lap and when the driver leaves) and restores it for the crew-mate: laps, total time, best lap and
last lap. The total time is the race clock at the last completed lap, so the time the car stood still during
the swap counts by itself. The new driver's game starts with a fresh car (fuel, tyres, no damage); rules such
as a minimum swap time are for the stewards, who find every connect and disconnect in `connections`.

Two things about the server matter for swaps, both checked by the tests that run a race on a real server
(`tests/DDLink.ServerTests`). The race session must allow joining after the start (`IS_OPEN=1`): with
`IS_OPEN=2` the server refuses the crew-mate and skips the race as soon as fewer than two drivers are
connected. And even with `IS_OPEN=1` the server ends a race the moment nobody is connected, so a swap needs
at least one other car on the server at that moment.

## Bans

With `BansEndpoint` configured, the plugin asks the platform for its ban list every `BansIntervalSeconds`
(default 5): `GET <BansEndpoint>?eventId=<EventId>`, signed like a message over an empty body. The answer is

```json
{ "steamIds": ["76561198000000009"] }
```

The plugin writes these SteamIDs into the server's blacklist file (`blacklist.txt`, or whatever
`UserGroups[BlacklistUserGroup]` of `extra_cfg.yml` names), between two marker lines of its own. AssettoServer
watches that file: it reloads it, refuses banned drivers at the handshake and kicks those who are connected.
Lines outside the markers, for example what an admin banned on the server itself, stay as they are; the
server skips the marker lines because they are no numbers. The file is only written when its content changes.
A platform that does not answer, or answers with anything but such a list, changes nothing: the bans the
server knows stay. `tests/DDLink.ServerTests/BanTests.cs` checks all of it on a real server: within ten
seconds of a ban the SteamID is in the file, the connected driver is kicked and cannot join again, and a
lifted ban lets the driver back in.

## Classification rules

The server refreshes its own position field only when a lap is completed, so the plugin computes the order.

- Race: drivers who took the chequered flag first, ordered by laps (more is better), then total time.
  Drivers who did not take the flag follow in the same order and get `status: "notClassified"`.
- Race ties go to the driver who started further ahead. This matters most for first-lap retirements, who
  all have 0 laps and no time. The server builds the grid from the qualifying order, or from the entry list
  order when there was no qualifying, so race control decides that fallback by the order of the entry list
  (for example the order in which drivers entered).
- Practice and qualifying: ordered by best lap; drivers without a lap time come last as `notClassified`.
  Equal lap times keep the entry list order, because that is how the server itself orders the race grid.
- Drivers who never connected are not in the message. The platform knows the entry list and marks them as
  "did not start".

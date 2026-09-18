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
      "sectors": [35100] }
  ]
}
```

- `position` is the live rank. Race: more laps, then (once finished) the earlier finish, then the progress along
  the lap (`spline`, 0 to 1). Practice and qualifying: the best lap.
- `totalTimeMs` is the race clock when the car last crossed the line; gaps between cars on the same lap are the
  difference of these values.
- `x` and `z` are the world position on the ground plane in metres, `gear` is -1 for reverse and 0 for neutral,
  `gas` is the throttle in percent. Brake, fuel and tyre state are not known to the server.
- `sectors` are the sector times of the lap in progress, as far as they are set.

## Driver swaps

Assetto Corsa has no driver swap. A slot of the entry list can name several SteamIDs (`GUID=a;b;c`); the swap
is one driver leaving the server and a crew-mate joining the same slot. The server starts the slot's result
from zero when a different driver joins, so the plugin remembers what the car had achieved (at every
completed lap and when the driver leaves) and restores it for the crew-mate: laps, total time, best lap and
last lap. The total time is the race clock at the last completed lap, so the time the car stood still during
the swap counts by itself. The new driver's game starts with a fresh car (fuel, tyres, no damage); rules such
as a minimum swap time are for the stewards, who find every connect and disconnect in `connections`.

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

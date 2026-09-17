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
      "gridPosition": 2
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

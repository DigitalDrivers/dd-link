# dd-link

AssettoServer plugin that reports race results, laps, incidents and replays to the Digital Drivers platform.

## What it does

- Records laps (with cut counts), collisions (other car, impact speed, position) and connects/disconnects
  during every session.
- When a session ends, computes the final classification from laps and total time and queues one signed
  JSON message. The format is described in [docs/payload.md](docs/payload.md).
- Delivers through a durable outbox: messages are written to disk first, signed with HMAC-SHA256 and
  retried until the platform accepts them, also across server restarts. A result is never lost.

Not yet implemented: saving and uploading the full race replay.

## Layout

| Path | What |
| --- | --- |
| `src/DDLink.Core/` | Classification, message contract, signing, outbox. No AssettoServer dependency, fully unit-tested. |
| `src/DDLinkPlugin/` | The AssettoServer plugin: hooks server events, configuration, delivery loop. |
| `tests/DDLink.Core.Tests/` | xUnit tests; the outbox is tested against a real local HTTP server. |
| `assettoserver.version` | AssettoServer version the plugin is built against. |

## Build and test

Requires the .NET 9 SDK and git.

```bash
scripts/check.sh    # unit tests, then builds the plugin against the pinned AssettoServer sources
```

The plugin ends up in `out/DDLinkPlugin/`. Copy that folder to the server's `plugins/` directory, add
`DDLinkPlugin` to `EnablePlugins` in `extra_cfg.yml` and create `plugin_dd_link_cfg.yml` next to it:

```yaml
Endpoint: https://digitaldrivers.club/api/link/messages
Secret: <at least 32 characters, shared with the platform>
EventId: <id of the platform event this server runs>
ServerId: race-1
SpoolDirectory: dd-link-spool
```

## License

GNU Affero General Public License v3.0, see [LICENSE](LICENSE). The plugin is built against
[AssettoServer](https://github.com/compujuckel/AssettoServer), which is licensed under the AGPL.

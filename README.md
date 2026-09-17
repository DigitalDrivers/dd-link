# dd-link

AssettoServer plugin that reports race results, laps, incidents and replays to the Digital Drivers platform.

## Status

Not started. Planned scope (milestone 1):

- On every session change, send the final classification (computed from laps and total time) as signed JSON.
- Report laps with cut counts, collisions (other car, impact speed, position), connects and disconnects.
- Save the full race replay through AssettoServer's ReplayPlugin and hand it to the platform.
- Sign every message with HMAC; spool to disk and retry when the platform is unreachable.

## License

GNU Affero General Public License v3.0, see [LICENSE](LICENSE). The plugin is built against
[AssettoServer](https://github.com/compujuckel/AssettoServer), which is licensed under the AGPL.

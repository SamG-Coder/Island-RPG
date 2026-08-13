# IslandRpg.NetworkingChecks

This is a fast executable regression harness for the multiplayer protocol and
authoritative server. It deliberately has no OpenTK or renderer dependency.

The suite must cover these boundaries as the corresponding production APIs are
introduced:

- protocol-version negotiation and rejection of incompatible clients;
- length-prefixed frame fragmentation/coalescing and maximum-frame rejection;
- sequence ordering across unsigned wrap-around and stale-input rejection;
- handshake validation, duplicate identity handling and disconnect cleanup;
- authoritative movement validation and server-tick consistency;
- bounded inbound/outbound queues and slow-client back-pressure;
- snapshot delta/baseline recovery and compact serialization round trips;
- action validation for inventory, combat, gathering, crafting, building and
  world interaction without trusting client-owned results;
- multiplayer chat ordering and visibility;
- two real loopback clients joining, moving and disconnecting cleanly.

Checks should be deterministic, use loopback only, and enforce short timeouts so
the complete suite remains suitable for every build.

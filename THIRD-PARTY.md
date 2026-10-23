# Third-party components

RED_ROUTE uses the official **amnezia-vpn/amnezia-box** source at commit
`16724484cfa00d9b2c14d8280dc055564e2e12ba` (release `v1.260910.0`).
Source: https://github.com/amnezia-vpn/amnezia-box/tree/16724484cfa00d9b2c14d8280dc055564e2e12ba
License: GNU General Public License v3 or later. The original notice is bundled as
`service/engine/AMNEZIA-BOX-LICENSE.txt`. An IPv6 endpoint formatting fix
(`net.JoinHostPort`) and a redirected-stdin graceful shutdown companion
(`scripts/engine/redroute_control.go`) are applied reproducibly by `scripts/build-engine.ps1`.
The FakeIP allocation path is also patched to commit its cursor and mapping before
returning a DNS answer, so a process crash cannot lose or reassign a cached address.
Regression tests are in `scripts/engine/fakeip_store_test.go` and run before each engine build.
The build includes `with_gvisor,with_quic,with_wireguard,with_awg`. Dependency
versions are recorded in the bundled `service/engine/build-info.txt` and the
upstream `go.mod`/`go.sum` at the pinned commit. Wintun driver resources and their
upstream notices are embedded by sing-tun; see its `internal/wintun` directory.

The C# components use Microsoft .NET/WPF and the NuGet packages
System.Security.Cryptography.ProtectedData and System.ServiceProcess.ServiceController
(10.0.0, MIT). The Windows installer is built with WiX Toolset 6.0.2
(Microsoft Reciprocal License). Geo-Aggregator supplies optional service lists:
https://github.com/Ground-Zerro/Geo-Aggregator (its upstream data sources and
their respective terms apply). No user profiles or secrets are supplied with RED_ROUTE.
The bundled initial lists for YouTube, Telegram, OpenAI, Discord and GitHub come
from `cveloy102/autoturn-macos/Resources/Routing/seed.json` (Geo-Aggregator data);
daily refresh replaces service lists while preserving separately added manual rules.

RED_ROUTE is an independent client and is not endorsed by the projects above.

The pinned amneziawg-go v3.1.20260828 dependency is copied into the engine source
archive and patched by scripts/engine/patch-awg.ps1. The patch refreshes S4 after
a blocked TUN read and relocates payload before encryption, avoiding loss of
the first packet after configuration. The complete patched module and its MIT
license are included in the corresponding engine source archive. Upstream
device packet/handshake/concurrency tests run before each build.

# Architecture

`RedRoute.App` is an unelevated WPF desktop application with a native WinForms
NotifyIcon menu. It owns UI settings and import/export. No browser shell.
`RedRoute.Core` contains validation, configuration generation and failover policy.
`RedRoute.Platform` contains DPAPI storage, bounded framed IPC, and service lists.
`RedRoute.Service` runs under LocalSystem, accepts only typed settings and the
commands configure/status/check/recover, and launches one fixed installed engine.
No executable path, command line or arbitrary JSON core config is accepted over IPC.

The MSI records the installing user SID in HKLM. The pipe DACL grants that SID,
SYSTEM and Administrators access and denies the Network SID. The client verifies
the pipe server's process image matches the installed service before sending profiles.
Service files under Program Files are not user-writable. Runtime
JSON and encrypted settings live under a hardened ProgramData directory.

`Off -> probe candidate (no system routes) -> verify HTTPS -> capture -> verify`
is the normal start sequence. A probe always uses an authenticated loopback SOCKS
inbound with a highest-priority route to the VPN outbound, including in Auto mode.
Health cannot succeed by accidentally checking an unlisted domain directly.
Candidate failure never leaves capture running. Three failed live probe cycles
stop capture before trying candidates. Status snapshots are immutable and include
bounded events, not raw engine logs. New configure/recover cancels the previous
operation; one serialized gate protects engine ownership.

AWG is an in-process userspace network endpoint inside the core (`useIntegratedTun=false`);
the only system TUN is the routing inbound. It uses `auto_route=true`,
`strict_route=false`; no kill switch or persistent firewall policy is installed.
Windows Job Object kill-on-close owns the child core, so a service crash releases
TUN handles. Normal stop and recovery also clear the Windows DNS resolver cache.
No physical adapter DNS, default gateway, proxy or third-party route is rewritten
by C# code. FakeIP domain identity avoids routing unrelated shared-CDN IPs wholesale.

DNS: local resolver for direct destinations/server bootstrap; DNS-over-HTTPS via
VPN for VPN destinations. Endpoint hostnames are resolved before starting capture.
The single-peer `AllowedIPs` remains authoritative; the client does not silently
expand a server profile's cryptographic destinations. IPv4-only profiles prefer
IPv4 DNS answers. Actual IPv6 acceptance needs live validation.

Before capture, the service records direct IPv6 source/route availability separately
from profile IPv6 support. Link-local and ULA addresses on a physical adapter do not
establish public IPv6 connectivity. The route check uses local UDP source selection
without transmitting a packet; it does not prove remote IPv6 reachability.

The IPv6 TUN stays present even with an IPv4-only profile, preventing a full-VPN
bypass through physical IPv6. Public IPv6 with no usable outbound is explicitly
rejected before sniff, with no_drop enabled so repeated attempts still fail quickly.
Private/LAN destinations, health probes and unmapped FakeIP domains are excluded.
With a dual-stack VPN on an IPv4-only uplink, selected IPv6 stays on VPN while an
unselected sniffed real AAAA is resolved to IPv4 using the direct resolver. A selected
cached AAAA on an IPv4-only VPN is resolved again through its VPN resolver. The core's
new resolve override is opt-in and applies only to IPv6 plus a recognized domain.

Physical interface/address/gateway/DNS changes cause the next service tick to release
capture and rebuild the connection with fresh capabilities. The RED_ROUTE adapter
is excluded from that comparison. Real browser, OS route and network-change acceptance
remain separate from the userspace and route-matcher tests.

Catalog refresh pins one Geo-Aggregator commit, bounds download sizes, rejects
unsafe source paths, and atomically replaces the cache only after all requested
lists pass validation. A failed refresh retains the old cache. Individual manual
rules are separate from service records and survive refreshes.

No network tests run from `--smoke-test`. It uses temporary in-memory settings,
does not start the polling timer/IPC, renders WPF to PNG and exits.

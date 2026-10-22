# Acceptance checklist

Automated checks in `tests/RedRoute.Tests` cover malformed conf/domain/CIDR input,
disallowed hooks and multiple peers, candidate ordering, non-capturing probes,
three consecutive failures, all-unavailable direct fallback, recovery, mode defaults,
bounded IPC, and engine schema validation for AWG Auto/On/probe and WireGuard.
WPF smoke test exercises domain entry/validation and renders all three pages.

Not yet executed on a real VPN/Windows installation:

1. Clean MSI install on Windows 10 22H2 and Windows 11 x64; standard-user app launch
   with administrative credentials supplied only to installer. Verify pipe OwnerSid.
2. Real AWG 1/1.5/2/3 conf and WireGuard import; wrong server/key; IPv6 literal server;
   DNS hostname endpoint. Confirm unsupported conf fields produce actionable errors.
3. Verify On public egress and local LAN; Auto selected site and unrelated site on
   same CDN; real DNS, browser DoH/ECH, IPv4-only and dual-stack profiles.
4. Disrupt active server while transfers run; verify 3 failed cycles, release to
   direct, next candidate. Fail all profiles; verify direct access while retrying.
   Restore a server; confirm recovery without UI. No silent successful probe via direct.
5. Stop/kill core and kill service; confirm no persistent blackhole and job cleanup.
   Normal recovery and independent elevated recovery shortcut must leave Off after restart.
6. Sleep/resume, Wi-Fi change, Ethernet/Wi-Fi transitions, Windows reboot, login
   without starting the UI; confirm configured mode and no endless retry storm.
7. Upgrade and uninstall while connected; service stop precedes file removal, default
   network returns, shortcut and service removed. Settings may remain encrypted on disk.
8. Report contains stage/timestamp/errors/routes/DNS but no key/config/traffic history.
   Non-owner and remote pipe access refused; oversize/slow clients time out.

Do not use the automated test result as proof of the live scenarios above.

## Local acceptance for the 0.1.3 changes

On the current Windows 11 computer, the 0.1.2 installer was run through its real
WPF UI: welcome, visible progress, and persistent successful completion. A real
saved profile completed an AWG handshake and HTTPS 204 after adding explicit
routing-core DNS resolution before AWG userspace dialing. Full On mode changed
public egress; Auto mode changed egress for a temporary selected diagnostic
domain while an unlisted destination retained direct egress. Both remained
Connected during the next monitoring interval. Tests restored Off and removed
the temporary rule without saving it to user settings. No IP or keys are recorded here.

WPF smoke validation checks that the expanded settings panel is fully visible
after opening; installer welcome/progress/completion are rendered separately.
Windows 10, network-disruption/failover live tests and forced-process crash recovery
remain unverified; the earlier checklist still applies to those scenarios.

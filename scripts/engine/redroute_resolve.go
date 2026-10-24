package route

import (
	"github.com/sagernet/sing-box/adapter"
	C "github.com/sagernet/sing-box/constant"
	R "github.com/sagernet/sing-box/route/rule"
	M "github.com/sagernet/sing/common/metadata"
)

// Opt-in for a domain on an IPv4-only outbound. An already cached real AAAA
// address must be resolved again using its chosen resolver; merely sniffing SNI leaves the
// original IPv6 destination unchanged. IP-only rules and ordinary IPv6 stay intact.
func redRouteResolveDestination(metadata *adapter.InboundContext, action *R.RuleActionResolve) {
	if !action.OverrideDestination || action.Strategy != C.DomainStrategyIPv4Only ||
		!metadata.Destination.IsIPv6() || !M.IsDomainName(metadata.Domain) {
		return
	}
	metadata.OriginDestination = metadata.Destination
	metadata.Destination = M.Socksaddr{Fqdn: metadata.Domain, Port: metadata.Destination.Port}
	metadata.DestinationAddresses = nil
}

package route

import (
	"net/netip"
	"testing"

	"github.com/sagernet/sing-box/adapter"
	C "github.com/sagernet/sing-box/constant"
	R "github.com/sagernet/sing-box/route/rule"
	M "github.com/sagernet/sing/common/metadata"
)

func TestRedRouteCachedIPv6UsesSelectedIPv4VPN(t *testing.T) {
	original := M.Socksaddr{Addr: netip.MustParseAddr("2001:db8::10"), Port: 443}
	metadata := adapter.InboundContext{Destination: original, Domain: "selected.example", DestinationAddresses: []netip.Addr{original.Addr}}
	redRouteResolveDestination(&metadata, &R.RuleActionResolve{OverrideDestination: true, Strategy: C.DomainStrategyIPv4Only})
	if metadata.Destination.Fqdn != "selected.example" || metadata.Destination.Port != 443 || metadata.Destination.Addr.IsValid() || metadata.OriginDestination != original || metadata.DestinationAddresses != nil {
		t.Fatalf("cached AAAA was not replaced safely: %+v", metadata)
	}
}

func TestRedRouteResolvePreservesOtherDestinations(t *testing.T) {
	for _, test := range []struct {
		name, host, domain string
		override           bool
		strategy           C.DomainStrategy
	}{
		{"ordinary IPv6", "2001:db8::10", "selected.example", false, C.DomainStrategyIPv4Only},
		{"dual stack VPN", "2001:db8::10", "selected.example", true, C.DomainStrategyPreferIPv4},
		{"IPv6 literal", "2001:db8::10", "", true, C.DomainStrategyIPv4Only},
		{"IP is not domain", "2001:db8::10", "2001:db8::10", true, C.DomainStrategyIPv4Only},
		{"IPv4", "192.0.2.10", "selected.example", true, C.DomainStrategyIPv4Only},
	} {
		t.Run(test.name, func(t *testing.T) {
			original := M.Socksaddr{Addr: netip.MustParseAddr(test.host), Port: 443}
			metadata := adapter.InboundContext{Destination: original, Domain: test.domain}
			redRouteResolveDestination(&metadata, &R.RuleActionResolve{OverrideDestination: test.override, Strategy: test.strategy})
			if metadata.Destination != original {
				t.Fatalf("unexpected rewrite: %+v", metadata.Destination)
			}
		})
	}
}

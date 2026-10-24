package route

import (
	"context"
	"encoding/json"
	"errors"
	"net/netip"
	"os"
	"syscall"
	"testing"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/dns/transport/fakeip"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	R "github.com/sagernet/sing-box/route/rule"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"
)

type redRouteDNS struct{ adapter.DNSRouter }

func (*redRouteDNS) LookupReverseMapping(netip.Addr) (string, bool) { return "", false }

type redRouteTransports struct{ adapter.DNSTransportManager }

type redRouteFakeTransport struct {
	adapter.FakeIPTransport
	store adapter.FakeIPStore
}

func (f *redRouteFakeTransport) Store() adapter.FakeIPStore { return f.store }

type redRouteFakeTransports struct {
	adapter.DNSTransportManager
	fake adapter.FakeIPTransport
}

func (m *redRouteFakeTransports) FakeIP() adapter.FakeIPTransport { return m.fake }

func (*redRouteTransports) FakeIP() adapter.FakeIPTransport { return nil }

// Consume configurations emitted by the actual C# builder, not a hand-copied rule.
// Use the real preliminary route matcher, before any browser payload is available.
func TestRedRouteGeneratedIPv6PreMatch(t *testing.T) {
	path := os.Getenv("RED_ROUTE_ROUTING_CASES")
	if path == "" {
		t.Skip("run scripts/test-ipv6.ps1 after the C# checks to test generated configurations")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var cases []struct {
		Name        string            `json:"name"`
		Rules       []json.RawMessage `json:"rules"`
		Destination string            `json:"destination"`
		Domain      string            `json:"domain"`
		Inbound     string            `json:"inbound"`
		User        string            `json:"user"`
		Reject      bool              `json:"reject"`
		Outbound    *string           `json:"outbound"`
	}
	if err := json.Unmarshal(data, &cases); err != nil {
		t.Fatal(err)
	}
	if len(cases) < 75 {
		t.Fatal("missing IPv6 policy cases")
	}
	for _, tc := range cases {
		t.Run(tc.Name, func(t *testing.T) {
			logger := log.NewNOPFactory().Logger()
			router := &Router{ctx: context.Background(), logger: logger, dns: &redRouteDNS{}, dnsTransport: &redRouteTransports{}}
			var fakeAddress netip.Addr
			if tc.Destination == "fakeip4" || tc.Destination == "fakeip6" {
				store := fakeip.NewStore(context.Background(), logger, netip.MustParsePrefix("198.18.0.0/15"), netip.MustParsePrefix("fc00::/18"))
				if err := store.Start(); err != nil {
					t.Fatal(err)
				}
				t.Cleanup(func() { _ = store.Close() })
				var err error
				fakeAddress, err = store.Create("selected.example", tc.Destination == "fakeip6")
				if err != nil {
					t.Fatal(err)
				}
				router.dnsTransport = &redRouteFakeTransports{fake: &redRouteFakeTransport{store: store}}
			}
			for _, raw := range tc.Rules {
				var opts option.Rule
				if err := opts.UnmarshalJSON(raw); err != nil {
					t.Fatal(err)
				}
				rule, err := R.NewRule(context.Background(), logger, opts, false)
				if err != nil {
					t.Fatal(err)
				}
				router.rules = append(router.rules, rule)
			}
			for _, network := range []string{N.NetworkTCP, N.NetworkUDP} {
				metadata := adapter.InboundContext{Inbound: tc.Inbound, User: tc.User, Network: network, Destination: M.ParseSocksaddr(tc.Destination), Domain: tc.Domain}
				if fakeAddress.IsValid() {
					metadata.Destination = M.Socksaddr{Addr: fakeAddress, Port: 443}
				}
				for i := 0; i < 100; i++ {
					err := router.PreMatch(metadata)
					if tc.Reject {
						if !errors.Is(err, syscall.ECONNREFUSED) {
							t.Fatalf("attempt %d: expected early refusal, got %v", i, err)
						}
					} else if err != nil {
						t.Fatalf("unexpected refusal: %v", err)
					}
				}
				if tc.Outbound != nil {
					selected, _, _, _, err := router.matchRule(context.Background(), &metadata, true, nil, nil)
					if err != nil {
						t.Fatal(err)
					}
					if selected == nil {
						t.Fatal("missing early route for explicit IPv6 selection")
					}
					action, ok := selected.Action().(*R.RuleActionRoute)
					if !ok || action.Outbound != *tc.Outbound {
						t.Fatalf("wrong selected outbound: %+v", selected.Action())
					}
				}
				if fakeAddress.IsValid() {
					_, _, _, _, err := router.matchRule(context.Background(), &metadata, true, nil, nil)
					if err != nil || !metadata.FakeIP || metadata.Destination.Fqdn != "selected.example" || metadata.OriginDestination.Addr != fakeAddress {
						t.Fatalf("FakeIP did not retain domain identity before public IPv6 filtering: %+v, %v", metadata, err)
					}
				}
			}
		})
	}
}

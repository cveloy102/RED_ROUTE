package fakeip

import (
	"errors"
	"net/netip"
	"reflect"
	"fmt"
	"sync"
	"testing"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing/common/logger"
)

type redRouteDurableStorage struct {
	adapter.FakeIPStorage
	order []string
	metadata *adapter.FakeIPMetadata
	mapping map[string]netip.Addr
	failMetadata, failMapping bool
}

func (s *redRouteDurableStorage) FakeIPLoadDomain(domain string, _ bool) (netip.Addr, bool) {
	address, ok := s.mapping[domain]
	return address, ok
}
func (s *redRouteDurableStorage) FakeIPSaveMetadata(metadata *adapter.FakeIPMetadata) error {
	s.order = append(s.order, "cursor")
	if s.failMetadata { return errors.New("simulated metadata write failure") }
	copy := *metadata; s.metadata = &copy
	return nil
}
func (s *redRouteDurableStorage) FakeIPStore(address netip.Addr, domain string) error {
	s.order = append(s.order, "mapping")
	if s.failMapping { return errors.New("simulated mapping write failure") }
	s.mapping[domain] = address
	return nil
}
func (s *redRouteDurableStorage) FakeIPStoreAsync(netip.Addr, string, logger.Logger) { s.order = append(s.order, "unsafe-async-mapping") }
func (s *redRouteDurableStorage) FakeIPSaveMetadataAsync(*adapter.FakeIPMetadata) { s.order = append(s.order, "unsafe-async-cursor") }
func redRouteStore(storage *redRouteDurableStorage) *Store {
	current := netip.MustParseAddr("198.18.0.2")
	if storage.metadata != nil { current = storage.metadata.Inet4Current }
	return &Store{storage: storage, inet4Range: netip.MustParsePrefix("198.18.0.0/15"), inet4Current: current}
}
func TestRedRouteDNSAnswerIsDurableBeforeDelivery(t *testing.T) {
	storage := &redRouteDurableStorage{mapping: make(map[string]netip.Addr)}
	address, err := redRouteStore(storage).Create("first.example.org", false)
	if err != nil || !address.IsValid() || !reflect.DeepEqual(storage.order, []string{"cursor", "mapping"}) { t.Fatalf("DNS answer was not durably committed before delivery: %v / %v", storage.order, err) }
	// Simulate process loss: only the committed mapping/cursor survive.
	restarted := redRouteStore(storage)
	newAddress, err := restarted.Create("second.example.org", false)
	if err != nil || newAddress == address { t.Fatal("restart reassigned a previously delivered address") }
	oldAddress, err := restarted.Create("first.example.org", false)
	if err != nil || oldAddress != address { t.Fatal("restart lost an already delivered DNS answer") }
}
func TestRedRouteFailedDNSCommitReturnsNoAnswer(t *testing.T) {
	for _, metadataFailure := range []bool{true, false} {
		storage := &redRouteDurableStorage{mapping: make(map[string]netip.Addr), failMetadata: metadataFailure, failMapping: !metadataFailure}
		address, err := redRouteStore(storage).Create("first.example.org", false)
		if err == nil || address.IsValid() { t.Fatal("returned a DNS answer that cannot survive a crash") }
		if metadataFailure && len(storage.order) != 1 { t.Fatal("stored mapping before allocation cursor was durable") }
	}
}
func TestRedRouteConcurrentDNSAllocationsRemainUnique(t *testing.T) {
	storage := &redRouteDurableStorage{mapping: make(map[string]netip.Addr)}
	store := redRouteStore(storage)
	var group sync.WaitGroup
	addresses := make(chan netip.Addr, 100)
	for i := 0; i < 100; i++ {
		group.Add(1)
		go func(index int) {
			defer group.Done()
			address, err := store.Create(fmt.Sprintf("%d.example.org", index), false)
			if err != nil { t.Error(err); return }
			addresses <- address
		}(i)
	}
	group.Wait(); close(addresses)
	unique := make(map[netip.Addr]bool)
	for address := range addresses { if unique[address] { t.Fatal("concurrent DNS requests reused an address") }; unique[address] = true }
	if len(unique) != 100 { t.Fatal("missing DNS answers") }
	if next, err := redRouteStore(storage).Create("after-restart.example.org", false); err != nil || unique[next] { t.Fatal("restart reused an address after concurrent DNS requests") }
}

package cachefile

import (
    "net/netip"
    "path/filepath"
    "testing"

    "github.com/sagernet/bbolt"
    "github.com/sagernet/sing-box/adapter"
)

// Model a restart that only reuses existing browser DNS answers, then crashes.
// Reading the allocation cursor must not consume the only durable copy.
func TestRedRouteCachedDNSMetadataSurvivesRepeatedRestarts(t *testing.T) {
    path := filepath.Join(t.TempDir(), "dns.db")
    open := func() *CacheFile {
        db, err := bbolt.Open(path, 0600, nil)
        if err != nil { t.Fatal(err) }
        return &CacheFile{DB: db}
    }
    address := netip.MustParseAddr("198.18.0.17")
    metadata := &adapter.FakeIPMetadata{
        Inet4Range: netip.MustParsePrefix("198.18.0.0/15"),
        Inet6Range: netip.MustParsePrefix("fc00::/18"),
        Inet4Current: address,
        Inet6Current: netip.MustParseAddr("fc00::2"),
    }
    cache := open()
    if err := cache.FakeIPSaveMetadata(metadata); err != nil { t.Fatal(err) }
    if err := cache.FakeIPStore(address, "cached.example.org"); err != nil { t.Fatal(err) }
    if err := cache.DB.Close(); err != nil { t.Fatal(err) }
    for restart := 0; restart < 4; restart++ {
        cache = open()
        got := cache.FakeIPMetadata()
        if got == nil || *got != *metadata { cache.DB.Close(); t.Fatalf("restart %d lost committed allocation metadata", restart) }
        gotAddress, found := cache.FakeIPLoadDomain("cached.example.org", false)
        if !found || gotAddress != address { cache.DB.Close(); t.Fatal("lost cached DNS mapping") }
        if domain, ok := cache.FakeIPLoad(address); !ok || domain != "cached.example.org" { cache.DB.Close(); t.Fatal("lost reverse DNS mapping") }
        // Close the DB only: no Store.Close/metadata write, as after process termination.
        if err := cache.DB.Close(); err != nil { t.Fatal(err) }
    }
}

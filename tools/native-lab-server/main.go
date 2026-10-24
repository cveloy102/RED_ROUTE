// Test-only userspace VPN peer. Never installed with RED_ROUTE.
package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/netip"
	"os"
	"strings"
	"sync/atomic"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
)

func main() {
	if len(os.Args) != 2 {
		panic("configuration required")
	}
	data, err := os.ReadFile(os.Args[1])
	if err != nil {
		panic(err)
	}
	var options struct {
		IPC  string
		Port int
	}
	if err = json.Unmarshal(data, &options); err != nil {
		panic(err)
	}
	tun, stack, err := netstack.CreateNetTUN([]netip.Addr{netip.MustParseAddr("192.0.2.2"), netip.MustParseAddr("2001:db8::2")}, nil, 1280)
	if err != nil {
		panic(err)
	}
	listener6, err := stack.ListenTCP(&net.TCPAddr{IP: net.ParseIP("2001:db8::2"), Port: options.Port})
	if err != nil {
		panic(err)
	}
	peer := device.NewDevice(tun, conn.NewDefaultBind(), device.NewLogger(device.LogLevelSilent, ""))
	defer peer.Close()
	if err = peer.IpcSet(options.IPC); err != nil {
		panic(err)
	}
	if err = peer.Up(); err != nil {
		panic(err)
	}
	listener, err := stack.ListenTCP(&net.TCPAddr{IP: net.ParseIP("192.0.2.2"), Port: options.Port})
	if err != nil {
		panic(err)
	}
	server := new(http.Server)
	var ipv6Unavailable atomic.Bool
	server.Handler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Connection", "close")
		if r.URL.Path == "/disable-ipv6" {
			ipv6Unavailable.Store(true)
			_, _ = w.Write([]byte("V6-DISABLED"))
			return
		}
		if r.URL.Path == "/enable-ipv6" {
			ipv6Unavailable.Store(false)
			_, _ = w.Write([]byte("V6-ENABLED"))
			return
		}
		remote, _, _ := net.SplitHostPort(r.RemoteAddr)
		if ipv6Unavailable.Load() && net.ParseIP(remote).To4() == nil {
			http.Error(w, "IPv6 unavailable", http.StatusServiceUnavailable)
			return
		}
		if r.URL.Path == "/large" {
			_, _ = w.Write([]byte(strings.Repeat("RED_ROUTE", 16384)))
			return
		}
		_, _ = w.Write([]byte("VPN-LAB"))
	})
	go func() { _ = server.Serve(listener) }()
	go func() { _ = server.Serve(listener6) }()
	fmt.Println("READY")
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		if strings.TrimSpace(scanner.Text()) == "RED_ROUTE_STOP" {
			break
		}
	}
	_ = server.Close()
}

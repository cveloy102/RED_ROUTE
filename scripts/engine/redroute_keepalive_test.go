package awg

import (
	"strings"
	"testing"
	"github.com/sagernet/sing-box/option"
	"github.com/sagernet/sing/common/json"
)

func TestRedRouteKeepaliveRange(t *testing.T) {
	for _, value := range []string{`25`, `"25"`, `"25-35"`, `0`, `"0-0"`, `65535`} {
		var opts option.AwgEndpointOptions
		data := `{"private_key":"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=","peers":[{"public_key":"AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=","persistent_keepalive_interval":`+value+`}]}`
		if err := json.Unmarshal([]byte(data), &opts); err != nil { t.Fatal(value, err) }
		ipc, err := genIpcConfig(opts)
		if err != nil { t.Fatal(err) }
		if !strings.Contains(ipc, "persistent_keepalive_interval="+strings.Trim(value, `"`)) { t.Fatal("interval was changed", ipc) }
		if err := newConfigTestDevice(t).IpcSet(ipc); err != nil { t.Fatal("native AWG rejected interval", err) }
		encoded, err := json.Marshal(opts)
		if err != nil { t.Fatal(err) }
		var again option.AwgEndpointOptions
		if err := json.Unmarshal(encoded, &again); err != nil || again.Peers[0].PersistentKeepaliveInterval != opts.Peers[0].PersistentKeepaliveInterval { t.Fatal("round trip failed", err) }
	}
	for _, value := range []string{`"35-25"`, `"-1"`, `"1-65536"`, `65536`, `true`, `null`, `""`, `1.5`, `"1-2-3"`} {
		var opts option.AwgPeerOptions
		if err := json.Unmarshal([]byte(`{"persistent_keepalive_interval":`+value+`}`), &opts); err == nil { t.Fatal("invalid interval accepted", value) }
	}
}

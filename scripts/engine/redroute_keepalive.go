package option

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
)

// AwgKeepaliveInterval preserves the range supported by amneziawg-go's UAPI.
// Legacy numeric JSON remains supported; WireGuard's separate option is unchanged.
type AwgKeepaliveInterval string

func (v *AwgKeepaliveInterval) UnmarshalJSON(data []byte) error {
	text := strings.TrimSpace(string(data))
	if strings.HasPrefix(text, `"`) {
		if err := json.Unmarshal(data, &text); err != nil { return err }
	}
	parts := strings.Split(text, "-")
	if len(parts) < 1 || len(parts) > 2 { return fmt.Errorf("invalid AWG keepalive interval") }
	lo, err := strconv.ParseUint(parts[0], 10, 16)
	if err != nil { return fmt.Errorf("invalid AWG keepalive interval") }
	hi := lo
	if len(parts) == 2 {
		hi, err = strconv.ParseUint(parts[1], 10, 16)
		if err != nil || hi < lo { return fmt.Errorf("invalid AWG keepalive range") }
	}
	text = strconv.FormatUint(lo, 10)
	if len(parts) == 2 { text += "-" + strconv.FormatUint(hi, 10) }
	*v = AwgKeepaliveInterval(text)
	return nil
}
func (v AwgKeepaliveInterval) String() string { return string(v) }
func (v AwgKeepaliveInterval) MarshalJSON() ([]byte, error) {
	if v == "" { return []byte("0"), nil }
	if strings.Contains(string(v), "-") { return json.Marshal(string(v)) }
	return []byte(v), nil
}

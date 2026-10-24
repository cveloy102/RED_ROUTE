// RED_ROUTE companion to the GPL-3.0-or-later CLI: graceful Windows child shutdown.
package main

import (
	"bufio"
	"os"
)

// Sent only after Box.Start has completed all endpoint/inbound/network stages.
// This fixed protocol line is independent of log level and contains no destinations.
func redRouteReady() { _, _ = os.Stdout.WriteString("RED_ROUTE_READY\n") }

// Only the parent owns redirected stdin. No network listener or filesystem command surface.
func redRouteControl(signals chan<- os.Signal) {
	go func() {
		scanner := bufio.NewScanner(os.Stdin)
		for scanner.Scan() {
			if scanner.Text() == "RED_ROUTE_STOP" {
				signals <- os.Interrupt
				return
			}
		}
	}()
}

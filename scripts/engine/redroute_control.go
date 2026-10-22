// RED_ROUTE companion to the GPL-3.0-or-later CLI: graceful Windows child shutdown.
package main

import (
    "bufio"
    "os"
)

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

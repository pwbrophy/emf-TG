//go:build !linux

package main

import "net"

// limitSendQueue: see sendqueue_linux.go. Elsewhere (the Windows PC used for
// development) a small fixed send buffer does roughly the same job.
func limitSendQueue(c net.Conn) {
	if tc, ok := c.(*net.TCPConn); ok {
		_ = tc.SetWriteBuffer(2 * viewerSendQueueBytes)
	}
}

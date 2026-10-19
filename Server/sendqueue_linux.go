//go:build linux

package main

import (
	"net"
	"syscall"
)

// TCP_NOTSENT_LOWAT (linux/tcp.h); not in the syscall package.
const tcpNotSentLowat = 25

// limitSendQueue keeps at most about one frame of a viewer's video waiting in
// the kernel. Without it the socket's send buffer grows to hold several frames
// whenever the Wi-Fi to that phone slows down, and the viewer's one-frame
// mailbox (video.go) never gets to drop stale frames: the picture falls up to
// a second behind and then fast-forwards. With it, writes wait until the
// previous frame has nearly gone, so a slow phone gets fewer frames, not old
// ones.
func limitSendQueue(c net.Conn) {
	tc, ok := c.(*net.TCPConn)
	if !ok {
		return
	}
	raw, err := tc.SyscallConn()
	if err != nil {
		return
	}
	_ = raw.Control(func(fd uintptr) {
		_ = syscall.SetsockoptInt(int(fd), syscall.IPPROTO_TCP, tcpNotSentLowat, viewerSendQueueBytes)
	})
}

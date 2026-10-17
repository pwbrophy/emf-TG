// Package mjpeg reads and writes MJPEG streams (multipart/x-mixed-replace),
// the format the tanks' cameras serve and that browsers show in an <img>.
//
// Frames are read by Content-Length rather than by searching for the next
// boundary, so each frame is available the moment its last byte arrives
// instead of one frame interval later when the following boundary shows up.
package mjpeg

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"net/textproto"
	"strconv"
	"strings"
)

// Boundary used for every stream we write.
const Boundary = "frame"

// ContentType is the Content-Type header value for streams we write.
const ContentType = "multipart/x-mixed-replace; boundary=" + Boundary

// TimestampHeader carries the time a frame was sent by its source (Unix
// nanoseconds), passed through untouched so a test tool can measure the
// end-to-end delay. Browsers ignore unknown part headers.
const TimestampHeader = "X-Timestamp"

var (
	firstBoundary = []byte("--" + Boundary + "\r\n")
	endBoundary   = []byte("\r\n--" + Boundary + "\r\n")
)

// Frame is one JPEG image plus the source's send timestamp, if any.
type Frame struct {
	Data      []byte
	Timestamp string
}

// WriteStart writes the opening boundary. Call once before the first frame.
func WriteStart(w io.Writer) error {
	_, err := w.Write(firstBoundary)
	return err
}

// WriteFrame writes one part and then the boundary that closes it. Browsers
// only display a part once they see the boundary that ends it, so writing it
// straight after the JPEG shows each frame as soon as it arrives.
func WriteFrame(w io.Writer, f Frame) error {
	hdr := "Content-Type: image/jpeg\r\nContent-Length: " + strconv.Itoa(len(f.Data)) + "\r\n"
	if f.Timestamp != "" {
		hdr += TimestampHeader + ": " + f.Timestamp + "\r\n"
	}
	hdr += "\r\n"
	if _, err := io.WriteString(w, hdr); err != nil {
		return err
	}
	if _, err := w.Write(f.Data); err != nil {
		return err
	}
	_, err := w.Write(endBoundary)
	return err
}

// ReadFrames reads frames from an MJPEG stream and calls fn for each one,
// until the stream ends, fn returns an error, or a part is malformed.
// Parts must carry a Content-Length (the tanks' firmware sends one).
func ReadFrames(r io.Reader, maxFrameBytes int, fn func(Frame) error) error {
	br := bufio.NewReaderSize(r, 32*1024)
	tp := textproto.NewReader(br)
	for {
		// Skip forward to the next boundary line ("--frame"). The blank line
		// that ends the previous part's data is skipped here too.
		for {
			line, err := tp.ReadLine()
			if err != nil {
				return eofIsNil(err)
			}
			if strings.HasPrefix(line, "--") {
				break
			}
		}

		hdr, err := tp.ReadMIMEHeader()
		if err != nil {
			return eofIsNil(err)
		}
		n, err := strconv.Atoi(hdr.Get("Content-Length"))
		if err != nil || n <= 0 {
			return fmt.Errorf("mjpeg: part without a valid Content-Length")
		}
		if n > maxFrameBytes {
			return fmt.Errorf("mjpeg: frame of %d bytes is over the %d byte limit", n, maxFrameBytes)
		}

		data := make([]byte, n)
		if _, err := io.ReadFull(br, data); err != nil {
			return eofIsNil(err)
		}
		if err := fn(Frame{Data: data, Timestamp: hdr.Get(TimestampHeader)}); err != nil {
			return err
		}
	}
}

func eofIsNil(err error) error {
	if errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
		return nil
	}
	return err
}

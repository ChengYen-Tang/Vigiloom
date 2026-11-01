package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"github.com/gorilla/websocket"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"testing"
	"time"
)

func TestBudgetBoundsReadsAndWrites(t *testing.T) {
	left, right := net.Pipe()
	defer left.Close()
	defer right.Close()
	b := &byteBudget{limit: 4}
	conn := &budgetConn{Conn: left, budget: b}
	go func() { io.Copy(io.Discard, right) }()
	n, err := conn.Write([]byte("abcdefgh"))
	if n != 4 || err != errBudget {
		t.Fatalf("write=%d,%v", n, err)
	}
	used, hit := b.snapshot()
	if used != 4 || !hit {
		t.Fatalf("budget=%d,%v", used, hit)
	}
}
func TestEndpointValidation(t *testing.T) {
	for _, value := range []string{"ws://x.measurement-lab.org/ndt/v7/upload", "wss://localhost/ndt/v7/upload", "wss://x.measurement-lab.org:80/ndt/v7/upload", "wss://x.measurement-lab.org/other", "wss://user@x.measurement-lab.org/ndt/v7/upload"} {
		if _, err := validateURL(value, "/ndt/v7/upload"); err == nil {
			t.Fatalf("accepted %s", value)
		}
	}
	for _, ip := range []string{"127.0.0.1", "::1", "10.0.0.1", "100.64.0.1", "169.254.1.1", "192.0.2.3", "fc00::1", "::ffff:127.0.0.1"} {
		if publicIP(netip.MustParseAddr(ip)) {
			t.Fatal(ip)
		}
	}
	if !publicIP(netip.MustParseAddr("8.8.8.8")) {
		t.Fatal("public address rejected")
	}
}
func TestNoServerDoesNotDial(t *testing.T) {
	result := run(context.Background(), request{MaxBytes: 1 << 20}, func(context.Context, *byteBudget) (target, error) { return target{}, io.EOF }, func(context.Context, string, string) (net.Conn, error) { t.Fatal("unexpected dial"); return nil, nil }, nil)
	if result.Status != "NoServer" || result.DownloadMbps != nil {
		t.Fatalf("%+v", result)
	}
}
func TestReferenceClientUsesTLSAndStopsAtBudget(t *testing.T) {
	upgrader := websocket.Upgrader{Subprotocols: []string{"net.measurementlab.ndt.v7"}}
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		conn, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		defer conn.Close()
		if r.URL.Path == "/ndt/v7/download" {
			data := make([]byte, 32768)
			for i := 0; i < 8; i++ {
				if conn.WriteMessage(websocket.BinaryMessage, data) != nil {
					return
				}
			}
			conn.WriteControl(websocket.CloseMessage, websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""), time.Now().Add(time.Second))
		} else {
			var total int64
			start := time.Now()
			for {
				_, data, err := conn.ReadMessage()
				if err != nil {
					return
				}
				total += int64(len(data))
				message := map[string]any{"AppInfo": map[string]int64{"NumBytes": total, "ElapsedTime": time.Since(start).Microseconds()}, "TCPInfo": map[string]int64{"MinRTT": 1200}}
				payload, _ := json.Marshal(message)
				if conn.WriteMessage(websocket.TextMessage, payload) != nil {
					return
				}
			}
		}
	}))
	defer server.Close()
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	template := &x509.Certificate{SerialNumber: big.NewInt(1), DNSNames: []string{"fixture.measurement-lab.org"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, _ := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	certificate, _ := x509.ParseCertificate(der)
	roots := x509.NewCertPool()
	roots.AddCert(certificate)
	server.TLS = &tls.Config{Certificates: []tls.Certificate{{Certificate: [][]byte{der}, PrivateKey: key}}}
	server.StartTLS()
	find := func(context.Context, *byteBudget) (target, error) {
		return target{URLs: map[string]string{
			"wss:///ndt/v7/download": "wss://fixture.measurement-lab.org/ndt/v7/download",
			"wss:///ndt/v7/upload":   "wss://fixture.measurement-lab.org/ndt/v7/upload"}}, nil
	}
	// Use fixture hostname for certificate verification; certificate has example.com.
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	answer := run(ctx, request{MaxBytes: 1 << 20, TimeoutSeconds: 3}, find, func(ctx context.Context, network, address string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, network, server.Listener.Addr().String())
	}, roots)
	if answer.Bytes > 1<<20 {
		t.Fatalf("exceeded budget: %d", answer.Bytes)
	}
	if answer.Status != "BudgetExceeded" {
		t.Fatalf("expected budget stop: %+v", answer)
	}
	if answer.DownloadMbps == nil || *answer.DownloadMbps <= 0 {
		t.Fatalf("download absent: %+v", answer)
	}
}

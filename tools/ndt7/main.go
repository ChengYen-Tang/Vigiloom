package main

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	ndt7 "github.com/m-lab/ndt7-client-go"
	"github.com/m-lab/ndt7-client-go/spec"
)

const locateURL = "https://locate.measurementlab.net/v2/nearest/ndt/ndt7"
const version = "1.0.0"

var errBudget = errors.New("budget_exhausted")

type request struct {
	MaxBytes       int64
	TimeoutSeconds int
}
type result struct {
	Status              string
	DownloadMbps        *float64 `json:",omitempty"`
	UploadMbps          *float64 `json:",omitempty"`
	LatencyMilliseconds *float64 `json:",omitempty"`
	Bytes               int64
	Server              string   `json:",omitempty"`
	City                string   `json:",omitempty"`
	Country             string   `json:",omitempty"`
	Latitude            *float64 `json:",omitempty"`
	Longitude           *float64 `json:",omitempty"`
}
type target struct {
	Machine  string            `json:"machine"`
	URLs     map[string]string `json:"urls"`
	Location struct {
		City      string   `json:"city"`
		Country   string   `json:"country"`
		Latitude  *float64 `json:"latitude"`
		Longitude *float64 `json:"longitude"`
	} `json:"location"`
}

type byteBudget struct {
	mu          sync.Mutex
	limit, used int64
	hit         bool
}

func (b *byteBudget) reserve(wanted int) int {
	b.mu.Lock()
	defer b.mu.Unlock()
	available := b.limit - b.used
	if available <= 0 {
		b.hit = true
		return 0
	}
	if int64(wanted) > available {
		wanted = int(available)
	}
	b.used += int64(wanted)
	return wanted
}
func (b *byteBudget) finish(reserved, actual int) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.used -= int64(reserved - actual)
}
func (b *byteBudget) snapshot() (int64, bool) { b.mu.Lock(); defer b.mu.Unlock(); return b.used, b.hit }

type budgetConn struct {
	net.Conn
	budget *byteBudget
}

func (c *budgetConn) Read(p []byte) (int, error) {
	allowed := c.budget.reserve(len(p))
	if allowed == 0 {
		c.Conn.Close()
		return 0, errBudget
	}
	n, err := c.Conn.Read(p[:allowed])
	c.budget.finish(allowed, n)
	return n, err
}
func (c *budgetConn) Write(p []byte) (int, error) {
	allowed := c.budget.reserve(len(p))
	if allowed == 0 {
		c.Conn.Close()
		return 0, errBudget
	}
	n, err := c.Conn.Write(p[:allowed])
	c.budget.finish(allowed, n)
	if err == nil && n < len(p) {
		c.budget.mu.Lock()
		c.budget.hit = true
		c.budget.mu.Unlock()
		c.Conn.Close()
		return n, errBudget
	}
	return n, err
}

func publicIP(ip netip.Addr) bool {
	ip = ip.Unmap()
	if !ip.IsGlobalUnicast() || ip.IsPrivate() || ip.IsLoopback() || ip.IsLinkLocalUnicast() {
		return false
	}
	for _, p := range []string{"0.0.0.0/8", "100.64.0.0/10", "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "240.0.0.0/4", "2001:db8::/32", "fc00::/7"} {
		if netip.MustParsePrefix(p).Contains(ip) {
			return false
		}
	}
	return true
}
func validateURL(value, path string) (*url.URL, error) {
	u, err := url.Parse(value)
	if err != nil || u.Scheme != "wss" || u.User != nil || u.Fragment != "" || u.Path != path || (u.Port() != "" && u.Port() != "443") ||
		!strings.HasSuffix(strings.ToLower(u.Hostname()), ".measurement-lab.org") {
		return nil, errors.New("invalid_endpoint")
	}
	return u, nil
}
func dialPublic(ctx context.Context, network, address string) (net.Conn, error) {
	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip", host)
	if err != nil {
		return nil, err
	}
	if len(ips) == 0 {
		return nil, errors.New("no_address")
	}
	// Verify the full answer and pin the actual dial; no second DNS resolution.
	for _, ip := range ips {
		if !publicIP(ip) {
			return nil, errors.New("non_public_address")
		}
	}
	dialer := net.Dialer{Timeout: 7 * time.Second}
	return dialer.DialContext(ctx, network, net.JoinHostPort(ips[0].String(), port))
}
func discover(ctx context.Context, budget *byteBudget) (target, error) {
	client := http.Client{Timeout: 10 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return errors.New("redirect") }, Transport: &http.Transport{Proxy: nil, DialContext: dialPublic, DialTLSContext: func(dialCtx context.Context, network, address string) (net.Conn, error) {
		return dialTLS(ctx, dialCtx, network, address, dialPublic, budget, nil)
	}, TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12}}}
	defer client.CloseIdleConnections()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, locateURL, nil)
	if err != nil {
		return target{}, err
	}
	req.Header.Set("User-Agent", ndt7.MakeUserAgent("Vigiloom", version))
	response, err := client.Do(req)
	if err != nil {
		return target{}, err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return target{}, errors.New("locate_status")
	}
	var data struct {
		Results []target `json:"results"`
	}
	if err = json.NewDecoder(io.LimitReader(response.Body, 256*1024)).Decode(&data); err != nil {
		return target{}, err
	}
	for _, t := range data.Results {
		d, de := validateURL(t.URLs["wss:///ndt/v7/download"], "/ndt/v7/download")
		u, ue := validateURL(t.URLs["wss:///ndt/v7/upload"], "/ndt/v7/upload")
		if de == nil && ue == nil && d.Host == u.Host {
			return t, nil
		}
	}
	return target{}, errors.New("no_endpoint")
}
func dialTLS(lifetime, dialCtx context.Context, network, address string, dial func(context.Context, string, string) (net.Conn, error), budget *byteBudget, roots *x509.CertPool) (net.Conn, error) {
	raw, err := dial(dialCtx, network, address)
	if err != nil {
		return nil, err
	}
	stop := context.AfterFunc(lifetime, func() { raw.Close() })
	host, _, _ := net.SplitHostPort(address)
	secured := tls.Client(&budgetConn{Conn: raw, budget: budget}, &tls.Config{ServerName: host, MinVersion: tls.VersionTLS12, RootCAs: roots})
	if err = secured.HandshakeContext(dialCtx); err != nil {
		stop()
		raw.Close()
		return nil, err
	}
	return &cancelConn{Conn: secured, stop: stop}, nil
}

func mbps(m spec.Measurement) *float64 {
	if m.AppInfo == nil || m.AppInfo.NumBytes <= 0 || m.AppInfo.ElapsedTime <= 0 {
		return nil
	}
	value := 8 * float64(m.AppInfo.NumBytes) / float64(m.AppInfo.ElapsedTime) // bits/us = decimal Mbit/s
	return &value
}
func run(ctx context.Context, input request, find func(context.Context, *byteBudget) (target, error), dial func(context.Context, string, string) (net.Conn, error), roots *x509.CertPool) result {
	answer := result{Status: "Failed"}
	budget := &byteBudget{limit: input.MaxBytes}
	endpoint, err := find(ctx, budget)
	if err != nil {
		if errors.Is(ctx.Err(), context.Canceled) {
			answer.Status = "Cancelled"
		} else if ctx.Err() != nil {
			answer.Status = "Timeout"
		} else {
			answer.Status = "NoServer"
		}
		used, hit := budget.snapshot()
		answer.Bytes = used
		if hit {
			answer.Status = "BudgetExceeded"
		}
		return answer
	}
	down, de := validateURL(endpoint.URLs["wss:///ndt/v7/download"], "/ndt/v7/download")
	up, ue := validateURL(endpoint.URLs["wss:///ndt/v7/upload"], "/ndt/v7/upload")
	if de != nil || ue != nil || down.Host != up.Host {
		answer.Status = "InvalidServer"
		return answer
	}
	answer.Server = down.Hostname()
	answer.City = endpoint.Location.City
	answer.Country = endpoint.Location.Country
	answer.Latitude = endpoint.Location.Latitude
	answer.Longitude = endpoint.Location.Longitude
	client := ndt7.NewClient("Vigiloom", version)
	client.Scheme = "wss"
	client.Dialer.Proxy = nil
	client.Dialer.NetDialTLSContext = func(dialCtx context.Context, network, address string) (net.Conn, error) {
		return dialTLS(ctx, dialCtx, network, address, dial, budget, roots)
	}
	for index, service := range []*url.URL{down, up} {
		client.ServiceURL = service
		completed := make(chan error, 1)
		client.OnTestError = func(err error) { completed <- err }
		var measurements <-chan spec.Measurement
		if index == 0 {
			measurements, err = client.StartDownload(ctx)
		} else {
			measurements, err = client.StartUpload(ctx)
		}
		if err != nil {
			break
		}
		for m := range measurements {
			if index == 0 && m.Origin == spec.OriginClient {
				if speed := mbps(m); speed != nil {
					answer.DownloadMbps = speed
				}
			}
			if index == 1 && m.Origin == spec.OriginServer {
				if speed := mbps(m); speed != nil {
					answer.UploadMbps = speed
				}
			}
			if m.TCPInfo != nil && m.TCPInfo.MinRTT > 0 {
				value := float64(m.TCPInfo.MinRTT) / 1000
				if answer.LatencyMilliseconds == nil || value < *answer.LatencyMilliseconds {
					answer.LatencyMilliseconds = &value
				}
			}
		}
		err = <-completed
		if err != nil && !websocket.IsCloseError(err, websocket.CloseNormalClosure) {
			break
		}
		err = nil
		if ctx.Err() != nil {
			break
		}
	}
	used, exhausted := budget.snapshot()
	answer.Bytes = used
	switch {
	case exhausted || errors.Is(err, errBudget):
		answer.Status = "BudgetExceeded"
	case errors.Is(ctx.Err(), context.Canceled):
		answer.Status = "Cancelled"
	case ctx.Err() != nil:
		answer.Status = "Timeout"
	case err != nil:
		answer.Status = "Failed"
	case answer.DownloadMbps != nil && answer.UploadMbps != nil:
		answer.Status = "Success"
	default:
		answer.Status = "Partial"
	}
	return answer
}

type cancelConn struct {
	net.Conn
	stop func() bool
}

func (c *cancelConn) Close() error { c.stop(); return c.Conn.Close() }
func main() {
	input := request{}
	decoder := json.NewDecoder(io.LimitReader(os.Stdin, 4096))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil || input.MaxBytes < 1024*1024 || input.MaxBytes > 200*1024*1024 || input.TimeoutSeconds < 5 || input.TimeoutSeconds > 60 {
		json.NewEncoder(os.Stdout).Encode(result{Status: "InvalidInput"})
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(input.TimeoutSeconds)*time.Second)
	defer cancel()
	go func() { io.Copy(io.Discard, os.Stdin); cancel() }()
	answer := run(ctx, input, discover, dialPublic, nil)
	json.NewEncoder(os.Stdout).Encode(answer)
}

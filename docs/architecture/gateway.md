# Gateway routing and trusted proxies

The Gateway's `ReverseProxy` configuration controls destinations. The default Web, Agent, and OTLP routes all point to the existing Web host; configure their service endpoints separately as those services are extracted. The Gateway uses .NET service discovery: `Services:<name>:http` or `https` can list one or more endpoints, while deployments can also provide a resolvable service name.

When Web runs behind Gateway, configure `Vigiloom:Gateway:TrustedProxies` with the Gateway's connecting IP addresses. Web accepts the forwarded client IP, scheme, and host only from these addresses, before authentication and rate limiting. Without an explicit list Web does not process these headers. The local Development configuration trusts loopback for the local Gateway; production deployments must set the correct private addresses. Protect the network path between Gateway and Web and configure TLS and allowed public host names for the environment.

The Gateway's current route configuration forwards existing traffic; per-route authorization and the remaining microservice integrations are still being implemented under REQ-42. Backend authorization remains in effect.

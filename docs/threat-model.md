# Threat model and boundaries

Destinations are administrator configuration only; no request parameter can select a URL. Validation accepts only absolute HTTP/HTTPS destinations. TLS verification remains enabled by the platform. Client identity headers are never used for quota identity; verified JWT claims are preferred, with the socket address as the anonymous fallback.

The service is application-layer traffic governance. It is not a DDoS scrubbing service or a complete WAF. Deploy behind a trusted proxy chain and configure forwarded-header trust explicitly before enabling it. Keep `/admin` on a loopback or private management listener and protect it with a dedicated authorization policy and CSRF protection at the edge.

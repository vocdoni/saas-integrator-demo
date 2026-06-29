---
title: SDKs and tools
lead: 'There are two ways to integrate Vocdoni: the REST API documented here, and the lower-level TypeScript SDK. Pick the one that matches how much control you need.'
group: get_started
order: 30
reference:
  title: References and repositories
  columns: 2
  items:
    - title: TypeScript SDK
      description: Install the SDK and follow its guides.
      href: '{{SDK_URL}}'
      icon: terminal
      external: true
    - title: OpenAPI specification
      description: The raw swagger spec to generate clients.
      href: '{{SWAGGER_URL}}'
      icon: file-json
      external: true
    - title: GitHub
      description: Open-source repositories, issues and examples.
      href: '{{GITHUB_URL}}'
      icon: github
      external: true
    - title: Developer portal
      description: Guides, protocol docs and the ballot protocol.
      href: 'https://developer.vocdoni.io/'
      icon: book-open
      external: true
---

There are two ways to integrate with Vocdoni: the documented **REST API**, and a lower-level
**TypeScript SDK** for the voter's browser. Choose based on how much control you need — and combine
them where it makes sense.

## When to use the REST API

The SaaS REST API is the quickest path for most teams. It manages organizations, members, censuses,
processes, and results, handles the protocol cryptography internally, and works from any language with
an HTTP client. Use it when you want **managed elections without operating protocol internals** — it's
what every page in this documentation targets, and it covers the entire server-side lifecycle from
provisioning a tenant to reading a tally.

## When to use the SDK

Casting a ballot is voter-facing cryptography: encoding the ballot, authenticating to the Credential
Service Provider (CSP), and signing the vote transaction. That part runs **client-side**, in the
voter's browser, via the TypeScript SDK. The SDK talks **only to this REST API** — it never reaches
the chain directly — and is the other half of the casting flow described in
[Voting processes](/developers/docs/voting-processes#casting-a-vote).

Reach for it when you build a custom voting client, or need fine-grained control over the
authentication, ballot encoding, and vote-submission steps. The current SDK ships as small,
tree-shakeable packages — `@vocdoni/api-client` (typed HTTP client) and `@vocdoni/api-voting` (CSP
auth, ballot encoding, vote signing) — replacing the older monolithic `@vocdoni/sdk`.

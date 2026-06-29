# Developer tools

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
[Voting processes](../core-concepts/voting-processes.md#casting-a-vote).

Reach for it when you build a custom voting client, or need fine-grained control over the
authentication, ballot encoding, and vote-submission steps.

## References and repositories

| Resource | What it is |
|----------|------------|
| **[`@vocdoni/integrator-sdk`](https://github.com/vocdoni/integrator-sdk)** | The current TypeScript SDK for the voter side. Small, tree-shakeable packages: `@vocdoni/api-client` (typed HTTP client) and `@vocdoni/api-voting` (CSP auth, ballot encoding, vote signing). Replaces the older monolithic `@vocdoni/sdk`. |
| **OpenAPI specification** | The raw machine-readable spec for every endpoint, schema, and field — generate clients from it: [`vocdoni.github.io/saas-backend/swagger.yaml`](https://vocdoni.github.io/saas-backend/swagger.yaml). |
| **GitHub** | Open-source repositories, issues, and examples: [`github.com/vocdoni`](https://github.com/vocdoni). |
| **Developer portal** | Guides, protocol docs, and the ballot protocol reference: [`developer.vocdoni.io`](https://developer.vocdoni.io/). |

/**
 * The rate limits the deployments this suite starts are given.
 *
 * Vistara ships a per-client budget sized for one visitor. This harness is not
 * one visitor: three browser engines run a full pass one after another through
 * a single loopback address, so every request in the run lands in one bucket,
 * and a suite that grew by one spec started failing unrelated assertions with
 * `429` rather than with anything it meant to prove.
 *
 * These are the hosted profile's rates, restated for a loopback run. Both
 * ceilings are declared, not just the persisted one: the platform buckets and
 * the framework limiter that counts the same peer in front of them, because a
 * deployment that raises one without the other has a ceiling it can never
 * reach, and the platform refuses to start rather than pretend otherwise. The
 * partition is stated rather than implied — which is what raising any bucket
 * requires — and stated as the per-client mode this run already is: nothing is
 * behind a proxy here, and the one client being counted is the run itself.
 *
 * `tests/Vistara.E2E.HostTests` holds these values to the shipped hosted
 * profile and proves them against the composed pipeline, so they cannot drift
 * from the deployment they are copied from. Throttling behaviour itself stays
 * covered where it belongs, in the API's own tests.
 */
export const suiteRateLimits: Record<string, string> = {
  Platform__RateLimits__PartitionMode: 'ForwardedClient',
  Platform__RateLimits__Window: '00:01:00',
  Platform__RateLimits__Api: '6000',
  Platform__RateLimits__Events: '600',
  Platform__RateLimits__Delivery: '6000',
  Platform__RateLimits__Media: '6000',
  Security__Limits__RequestsPerWindow: '6000',
  Security__Limits__RateLimitWindow: '00:01:00',
};

/**
 * The rate limits the deployments this suite starts are given.
 *
 * Vistara ships a per-client budget sized for one visitor. This harness is not
 * one visitor: three browser engines run one after another through a single
 * loopback address, so every request in the run lands in one bucket, and a
 * suite that grew by one spec would start failing unrelated assertions with
 * `429` rather than with anything it meant to prove.
 *
 * The partition is declared rather than left implied, which is what the
 * platform requires before any bucket is raised, and it is declared as the
 * per-client mode the deployment already uses: no proxy is trusted here, and
 * the single client being counted is the run itself. Throttling behaviour is
 * covered where it belongs, in the API's own tests, rather than by whichever
 * end-to-end assertion happens to be running when the budget runs out.
 */
export const suiteRateLimits: Record<string, string> = {
  Platform__RateLimits__PartitionMode: 'ForwardedClient',
  Platform__RateLimits__Window: '00:01:00',
  Platform__RateLimits__Api: '6000',
  Platform__RateLimits__Events: '600',
  Platform__RateLimits__Delivery: '6000',
  Platform__RateLimits__Media: '6000',
};
